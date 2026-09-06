using System.Security.Cryptography;
using System.Text;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure;

public interface IWorkspaceIsolationEgressGuard
{
    ValueTask<WorkspaceIsolationEgressGuardArmResult> ArmAsync(
        WorkspaceInstanceId workspaceId,
        WorkspaceIsolationBinding binding,
        NetworkConnectionProfile connection,
        CancellationToken cancellationToken,
        SecretMaterial? transientPassword = null);

    ValueTask<NetworkConnectionResult<Unit>> DisarmAsync(
        WorkspaceInstanceId workspaceId,
        WorkspaceIsolationBinding binding,
        CancellationToken cancellationToken);
}

public sealed record WorkspaceIsolationEgressGuardArmResult
{
    private WorkspaceIsolationEgressGuardArmResult(
        bool isEnforced,
        NetworkConnectionError? error)
    {
        if (!isEnforced && error is null)
        {
            throw new ArgumentException(
                "A successful isolation egress guard must be enforced.",
                nameof(isEnforced));
        }

        IsEnforced = isEnforced;
        Error = error;
    }

    public bool IsEnforced { get; }

    public NetworkConnectionError? Error { get; }

    public static WorkspaceIsolationEgressGuardArmResult Enforced() => new(true, null);

    public static WorkspaceIsolationEgressGuardArmResult Failed(
        NetworkConnectionError error,
        bool isEnforced) =>
        new(isEnforced, error ?? throw new ArgumentNullException(nameof(error)));
}

internal static class WorkspaceIsolationNetworkNames
{
    public const string Namespace = "ghostshell-vpn";

    public static string TunnelInterface(NetworkConnectionId connectionId)
    {
        var source = Encoding.UTF8.GetBytes(connectionId.Value);
        try
        {
            return $"gs{Convert.ToHexString(SHA256.HashData(source))[..10].ToLowerInvariant()}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(source);
        }
    }
}

/// <summary>
/// Enforces an isolated workspace route at the guest output boundary. VPN traffic is moved
/// through a private Linux network namespace; proxy traffic is transparently intercepted.
/// Route transitions install a blocking output policy before changing the active transport.
/// </summary>
public sealed class WorkspaceIsolationEgressGuard : IWorkspaceIsolationEgressGuard
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private const string RootFunction =
        "as_root() { if [ \"$(id -u)\" -eq 0 ]; then \"$@\"; else sudo -n \"$@\"; fi; }; ";

    private const string ArmScript = RootFunction + """
        set -eu
        tunnel=$1
        namespace=ghostshell-vpn
        state=/run/ghostshell-network-guard
        main_veth=gs-main
        vpn_veth=gs-vpn
        guard_installed=0
        preserve_guard_status() {
            status=$?
            trap - EXIT
            if [ "$status" -ne 0 ] && [ "$guard_installed" -eq 1 ]; then exit 78; fi
            exit "$status"
        }
        trap preserve_guard_status EXIT
        for tool in ip nft pkill sysctl; do
            command -v "$tool" >/dev/null 2>&1 || exit 69
        done
        if [ "$(id -u)" -ne 0 ]; then
            command -v sudo >/dev/null 2>&1 || exit 77
            sudo -n true >/dev/null 2>&1 || exit 77
        fi
        as_root sh -c '
            umask 077
            mkdir -p "$1"
            if [ ! -f "$1/ip-forward" ]; then sysctl -n net.ipv4.ip_forward > "$1/ip-forward"; fi
            if [ ! -f "$1/ip6-forward" ]; then sysctl -n net.ipv6.conf.all.forwarding > "$1/ip6-forward"; fi
        ' ghostshell-guard "$state"
        guard_rules='
            table inet ghostshell_guard {
                chain mark_output {
                    type route hook output priority mangle; policy accept;
                    oifname "lo" return
                    meta mark set 0x4753
                }
                chain block_output {
                    type filter hook output priority filter; policy drop;
                    oifname "lo" accept
                    oifname "gs-main" accept
                }
            }
        '
        if as_root nft list table inet ghostshell_guard >/dev/null 2>&1; then
            {
                printf '%s\n' 'delete table inet ghostshell_guard'
                printf '%s\n' "$guard_rules"
            } | as_root nft -f -
        else
            printf '%s\n' "$guard_rules" | as_root nft -f -
        fi
        as_root nft list chain inet ghostshell_guard mark_output >/dev/null
        as_root nft list chain inet ghostshell_guard block_output \
            | grep -Eq 'policy drop'
        guard_installed=1
        proxy_state="$state/proxy"
        proxy_uid=$(id -u ghostshell-net 2>/dev/null || true)
        if [ -n "$proxy_uid" ] && command -v pkill >/dev/null 2>&1; then
            as_root pkill -TERM -u "$proxy_uid" -x redsocks >/dev/null 2>&1 || true
            as_root pkill -TERM -u "$proxy_uid" -x socat >/dev/null 2>&1 || true
        fi
        as_root nft delete table ip ghostshell_proxy >/dev/null 2>&1 || true
        if as_root test -f "$proxy_state/resolv.conf"; then
            as_root sh -c 'cat "$1" > /etc/resolv.conf' \
                ghostshell-proxy-resolver "$proxy_state/resolv.conf"
        fi
        as_root rm -rf -- "$proxy_state"
        if as_root ip netns list | grep -Eq '^ghostshell-vpn([[:space:]]|$)'; then
            for process in $(as_root ip netns pids "$namespace" 2>/dev/null || true); do
                as_root kill "$process" >/dev/null 2>&1 || true
            done
            sleep 1
            for process in $(as_root ip netns pids "$namespace" 2>/dev/null || true); do
                as_root kill -9 "$process" >/dev/null 2>&1 || true
            done
            as_root ip netns delete "$namespace" >/dev/null 2>&1 || true
        fi
        as_root ip link delete "$main_veth" >/dev/null 2>&1 || true
        as_root ip netns add "$namespace"
        as_root ip link add "$main_veth" type veth peer name "$vpn_veth"
        as_root ip link set "$vpn_veth" netns "$namespace"
        as_root ip address add 169.254.254.1/30 dev "$main_veth"
        as_root ip -6 address add fd42:4753::1/64 dev "$main_veth"
        as_root ip link set "$main_veth" up
        as_root ip netns exec "$namespace" ip link set lo up
        as_root ip netns exec "$namespace" ip address add 169.254.254.2/30 dev "$vpn_veth"
        as_root ip netns exec "$namespace" ip -6 address add fd42:4753::2/64 dev "$vpn_veth"
        as_root ip netns exec "$namespace" ip link set "$vpn_veth" up
        as_root ip netns exec "$namespace" ip route replace default via 169.254.254.1 dev "$vpn_veth"
        as_root ip netns exec "$namespace" ip -6 route replace default via fd42:4753::1 dev "$vpn_veth"
        as_root sysctl -q -w net.ipv4.ip_forward=1
        as_root sysctl -q -w net.ipv6.conf.all.forwarding=1
        as_root ip netns exec "$namespace" sysctl -q -w net.ipv4.ip_forward=1
        as_root ip netns exec "$namespace" sysctl -q -w net.ipv6.conf.all.forwarding=1
        as_root ip rule delete priority 100 fwmark 0x4753 table 4242 >/dev/null 2>&1 || true
        as_root ip -6 rule delete priority 100 fwmark 0x4753 table 4242 >/dev/null 2>&1 || true
        as_root ip route replace table 4242 default via 169.254.254.2 dev "$main_veth"
        as_root ip -6 route replace table 4242 default via fd42:4753::2 dev "$main_veth"
        as_root ip rule add priority 100 fwmark 0x4753 table 4242
        as_root ip -6 rule add priority 100 fwmark 0x4753 table 4242
        as_root nft delete table ip ghostshell_vpn_nat >/dev/null 2>&1 || true
        as_root nft delete table ip6 ghostshell_vpn_nat >/dev/null 2>&1 || true
        printf '%s\n' '
            table ip ghostshell_vpn_nat {
                chain postrouting {
                    type nat hook postrouting priority srcnat; policy accept;
                    oifname "gs-main" masquerade
                    ip saddr 169.254.254.0/30 oifname != "gs-main" masquerade
                }
            }
        ' | as_root nft -f -
        printf '%s\n' '
            table ip6 ghostshell_vpn_nat {
                chain postrouting {
                    type nat hook postrouting priority srcnat; policy accept;
                    oifname "gs-main" masquerade
                    ip6 saddr fd42:4753::/64 oifname != "gs-main" masquerade
                }
            }
        ' | as_root nft -f -
        printf '
            table inet ghostshell_guard {
                chain forward {
                    type filter hook forward priority filter; policy drop;
                    iifname "gs-vpn" oifname "%s" accept
                    iifname "%s" oifname "gs-vpn" ct state established,related accept
                }
            }
            table ip ghostshell_vpn_nat {
                chain postrouting {
                    type nat hook postrouting priority srcnat; policy accept;
                    oifname "%s" masquerade
                }
            }
            table ip6 ghostshell_vpn_nat {
                chain postrouting {
                    type nat hook postrouting priority srcnat; policy accept;
                    oifname "%s" masquerade
                }
            }
        ' "$tunnel" "$tunnel" "$tunnel" "$tunnel" \
            | as_root ip netns exec "$namespace" nft -f -
        trap - EXIT
        exit 0
        """;

    private const string ProxyArmScript = RootFunction + """
        set -eu
        protocol=$1
        proxy_host=$2
        proxy_port=$3
        state=/run/ghostshell-network-guard
        proxy_state="$state/proxy"
        proxy_config="$proxy_state/redsocks.conf"
        guard_installed=0
        preserve_guard_status() {
            status=$?
            trap - EXIT
            if [ "$status" -ne 0 ] && [ "$guard_installed" -eq 1 ]; then exit 78; fi
            exit "$status"
        }
        trap preserve_guard_status EXIT
        for tool in ip nft getent pgrep pkill redsocks runuser; do
            command -v "$tool" >/dev/null 2>&1 || exit 69
        done
        if [ "$protocol" = https ]; then
            command -v socat >/dev/null 2>&1 || exit 69
            test -r /etc/ssl/certs/ca-certificates.crt || exit 69
        fi
        id ghostshell-net >/dev/null 2>&1 || exit 69
        if [ "$(id -u)" -ne 0 ]; then
            command -v sudo >/dev/null 2>&1 || exit 77
            sudo -n true >/dev/null 2>&1 || exit 77
        fi
        proxy_uid=$(id -u ghostshell-net)
        proxy_gid=$(id -g ghostshell-net)
        guard_rules=$(printf '
            table inet ghostshell_guard {
                chain block_output {
                    type filter hook output priority filter; policy drop;
                    oifname "lo" accept
                    meta skuid %s accept
                }
            }
        ' "$proxy_uid")
        if as_root nft list table inet ghostshell_guard >/dev/null 2>&1; then
            {
                printf '%s\n' 'delete table inet ghostshell_guard'
                printf '%s\n' "$guard_rules"
            } | as_root nft -f -
        else
            printf '%s\n' "$guard_rules" | as_root nft -f -
        fi
        as_root nft list chain inet ghostshell_guard block_output \
            | grep -Eq 'policy drop'
        guard_installed=1
        fail_enforced() {
            trap - EXIT
            exit "$1"
        }
        proxy_ip=$(as_root runuser -u ghostshell-net -- getent ahostsv4 "$proxy_host" \
            | awk '{ print $1; exit }')
        test -n "$proxy_ip" || fail_enforced 168
        stop_proxy_process() {
            pid_file=$1
            expected=$2
            pid=$(as_root cat "$pid_file" 2>/dev/null || true)
            case "$pid" in ''|*[!0-9]*) return 0;; esac
            actual=$(as_root cat "/proc/$pid/comm" 2>/dev/null || true)
            if [ "$actual" = "$expected" ]; then
                as_root kill "$pid" >/dev/null 2>&1 || true
                for wait_step in 1 2 3 4 5; do
                    as_root kill -0 "$pid" >/dev/null 2>&1 || break
                    sleep 1
                done
                as_root kill -9 "$pid" >/dev/null 2>&1 || true
            fi
        }
        stop_proxy_process "$proxy_state/redsocks.pid" redsocks
        stop_proxy_process "$proxy_state/socat.pid" socat
        as_root pkill -TERM -u "$proxy_uid" -x redsocks >/dev/null 2>&1 || true
        as_root pkill -TERM -u "$proxy_uid" -x socat >/dev/null 2>&1 || true
        as_root nft delete table ip ghostshell_proxy >/dev/null 2>&1 || true
        namespace=ghostshell-vpn
        if as_root ip netns list | grep -Eq '^ghostshell-vpn([[:space:]]|$)'; then
            for process in $(as_root ip netns pids "$namespace" 2>/dev/null || true); do
                as_root kill "$process" >/dev/null 2>&1 || true
            done
            as_root ip netns delete "$namespace" >/dev/null 2>&1 || true
        fi
        as_root ip link delete gs-main >/dev/null 2>&1 || true
        while as_root ip rule delete priority 100 fwmark 0x4753 table 4242 \
            >/dev/null 2>&1; do :; done
        while as_root ip -6 rule delete priority 100 fwmark 0x4753 table 4242 \
            >/dev/null 2>&1; do :; done
        as_root ip route flush table 4242 >/dev/null 2>&1 || true
        as_root ip -6 route flush table 4242 >/dev/null 2>&1 || true
        as_root nft delete table ip ghostshell_vpn_nat >/dev/null 2>&1 || true
        as_root nft delete table ip6 ghostshell_vpn_nat >/dev/null 2>&1 || true
        as_root sh -c 'umask 077; mkdir -p "$1"; cat > "$2"' \
            ghostshell-proxy "$proxy_state" "$proxy_config"
        as_root sed -i "s/GHOSTSHELL_PROXY_IP/$proxy_ip/g" "$proxy_config"
        as_root chown -R "$proxy_uid:$proxy_gid" "$proxy_state"
        if ! as_root test -f "$proxy_state/resolv.conf"; then
            as_root cp -L /etc/resolv.conf "$proxy_state/resolv.conf"
        fi
        printf '%s\n' 'nameserver 1.1.1.1' 'nameserver 8.8.8.8' \
            | as_root tee /etc/resolv.conf >/dev/null \
            || fail_enforced 172
        if [ "$protocol" = https ]; then
            as_root runuser -u ghostshell-net -- \
                socat TCP4-LISTEN:10081,bind=127.0.0.1,reuseaddr,fork \
                "OPENSSL-CONNECT:$proxy_ip:$proxy_port,verify=1,cafile=/etc/ssl/certs/ca-certificates.crt,commonname=$proxy_host" \
                >/dev/null 2>&1 &
            socat_pid=$!
            printf '%s\n' "$socat_pid" | as_root tee "$proxy_state/socat.pid" >/dev/null
            sleep 1
            as_root pgrep -u "$proxy_uid" -x socat >/dev/null 2>&1 \
                || fail_enforced 171
        fi
        as_root runuser -u ghostshell-net -- redsocks \
            -p "$proxy_state/redsocks.pid" -c "$proxy_config" \
            >/dev/null 2>&1
        sleep 1
        redsocks_pid=$(as_root cat "$proxy_state/redsocks.pid" 2>/dev/null || true)
        case "$redsocks_pid" in ''|*[!0-9]*) fail_enforced 171;; esac
        as_root kill -0 "$redsocks_pid" >/dev/null 2>&1 || fail_enforced 171
        printf '
            table ip ghostshell_proxy {
                chain redirect_output {
                    type nat hook output priority dstnat; policy accept;
                    meta skuid %s return
                    udp dport 53 redirect to :10053
                    ip daddr 127.0.0.0/8 return
                    meta l4proto tcp redirect to :10080
                }
            }
        ' "$proxy_uid" | as_root nft -f -
        printf '
            table inet ghostshell_guard {
                chain block_output {
                    type filter hook output priority filter; policy drop;
                    oifname "lo" accept
                    meta skuid %s accept
                    meta l4proto udp reject with icmpx type port-unreachable
                }
            }
        ' "$proxy_uid" | {
            printf '%s\n' 'delete table inet ghostshell_guard'
            cat
        } | as_root nft -f -
        as_root nft list chain ip ghostshell_proxy redirect_output \
            | grep -Eq 'tcp redirect to :10080'
        as_root nft list chain inet ghostshell_guard block_output \
            | grep -Eq 'policy drop'
        getent ahostsv4 example.com >/dev/null 2>&1 || fail_enforced 173
        trap - EXIT
        exit 0
        """;

    private const string DisarmScript = RootFunction + """
        set +e
        namespace=ghostshell-vpn
        state=/run/ghostshell-network-guard
        cleanup_failed=0
        try_cleanup() {
            "$@" >/dev/null 2>&1 || cleanup_failed=1
        }
        restore_sysctl() {
            key=$1
            file=$2
            value=$(as_root cat "$file" 2>/dev/null)
            if [ -z "$value" ]; then
                cleanup_failed=1
                return
            fi
            try_cleanup as_root sysctl -q -w "$key=$value"
            actual=$(as_root sysctl -n "$key" 2>/dev/null)
            if [ "$actual" != "$value" ]; then cleanup_failed=1; fi
        }
        for tool in ip nft sysctl; do
            command -v "$tool" >/dev/null 2>&1 || exit 69
        done
        if [ "$(id -u)" -ne 0 ]; then
            command -v sudo >/dev/null 2>&1 || exit 77
            sudo -n true >/dev/null 2>&1 || exit 77
        fi
        guard_present=0
        if as_root nft list table inet ghostshell_guard >/dev/null 2>&1; then
            guard_present=1
        fi
        proxy_state="$state/proxy"
        proxy_uid=$(id -u ghostshell-net 2>/dev/null || true)
        if [ -n "$proxy_uid" ]; then
            as_root pkill -TERM -u "$proxy_uid" -x redsocks >/dev/null 2>&1
            as_root pkill -TERM -u "$proxy_uid" -x socat >/dev/null 2>&1
        fi
        if as_root nft list table ip ghostshell_proxy >/dev/null 2>&1; then
            try_cleanup as_root nft delete table ip ghostshell_proxy
        fi
        if as_root nft list table ip ghostshell_proxy >/dev/null 2>&1; then
            cleanup_failed=1
        fi
        if as_root test -f "$proxy_state/resolv.conf"; then
            as_root sh -c 'cat "$1" > /etc/resolv.conf' \
                ghostshell-proxy-resolver "$proxy_state/resolv.conf" \
                || cleanup_failed=1
        fi
        if as_root ip netns list | grep -Eq '^ghostshell-vpn([[:space:]]|$)'; then
            for process in $(as_root ip netns pids "$namespace" 2>/dev/null); do
                as_root kill "$process" >/dev/null 2>&1
            done
            sleep 1
            for process in $(as_root ip netns pids "$namespace" 2>/dev/null); do
                as_root kill -9 "$process" >/dev/null 2>&1
            done
            as_root ip netns delete "$namespace" >/dev/null 2>&1
        fi
        if as_root ip netns list | grep -Eq '^ghostshell-vpn([[:space:]]|$)'; then
            cleanup_failed=1
        fi
        if as_root ip link show gs-main >/dev/null 2>&1; then
            try_cleanup as_root ip link delete gs-main
        fi
        if as_root ip link show gs-main >/dev/null 2>&1; then cleanup_failed=1; fi
        while as_root ip rule delete priority 100 fwmark 0x4753 table 4242 \
            >/dev/null 2>&1; do :; done
        while as_root ip -6 rule delete priority 100 fwmark 0x4753 table 4242 \
            >/dev/null 2>&1; do :; done
        if as_root ip rule show | grep -Eq 'fwmark 0x4753.*lookup 4242'; then
            cleanup_failed=1
        fi
        if as_root ip -6 rule show | grep -Eq 'fwmark 0x4753.*lookup 4242'; then
            cleanup_failed=1
        fi
        try_cleanup as_root ip route flush table 4242
        try_cleanup as_root ip -6 route flush table 4242
        if as_root nft list table ip ghostshell_vpn_nat >/dev/null 2>&1; then
            try_cleanup as_root nft delete table ip ghostshell_vpn_nat
        fi
        if as_root nft list table ip6 ghostshell_vpn_nat >/dev/null 2>&1; then
            try_cleanup as_root nft delete table ip6 ghostshell_vpn_nat
        fi
        if as_root nft list table ip ghostshell_vpn_nat >/dev/null 2>&1; then
            cleanup_failed=1
        fi
        if as_root nft list table ip6 ghostshell_vpn_nat >/dev/null 2>&1; then
            cleanup_failed=1
        fi
        if as_root test -f "$state/ip-forward" \
            && as_root test -f "$state/ip6-forward"; then
            restore_sysctl net.ipv4.ip_forward "$state/ip-forward"
            restore_sysctl net.ipv6.conf.all.forwarding "$state/ip6-forward"
        elif as_root test -f "$state/ip-forward" \
            || as_root test -f "$state/ip6-forward"; then
            cleanup_failed=1
        fi
        if [ "$cleanup_failed" -ne 0 ]; then exit 70; fi
        try_cleanup as_root rm -rf -- "$state"
        if as_root test -d "$state"; then cleanup_failed=1; fi
        if [ "$cleanup_failed" -ne 0 ]; then exit 70; fi
        as_root nft delete table inet ghostshell_guard >/dev/null 2>&1
        if as_root nft list table inet ghostshell_guard >/dev/null 2>&1; then exit 70; fi
        exit 0
        """;

    private readonly IWorkspaceIsolationProvider _isolationProvider;
    private readonly IWorkspaceIsolationCommandRunner _commandRunner;
    private readonly ISecretVault? _secretVault;

    public WorkspaceIsolationEgressGuard(IWorkspaceIsolationProvider isolationProvider)
        : this(isolationProvider, new WorkspaceIsolationCommandRunner())
    {
    }

    public WorkspaceIsolationEgressGuard(
        IWorkspaceIsolationProvider isolationProvider,
        ISecretVault secretVault)
        : this(
            isolationProvider,
            new WorkspaceIsolationCommandRunner(),
            secretVault)
    {
    }

    internal WorkspaceIsolationEgressGuard(
        IWorkspaceIsolationProvider isolationProvider,
        IWorkspaceIsolationCommandRunner commandRunner,
        ISecretVault? secretVault = null)
    {
        _isolationProvider = isolationProvider
            ?? throw new ArgumentNullException(nameof(isolationProvider));
        _commandRunner = commandRunner ?? throw new ArgumentNullException(nameof(commandRunner));
        _secretVault = secretVault;
    }

    public async ValueTask<WorkspaceIsolationEgressGuardArmResult> ArmAsync(
        WorkspaceInstanceId workspaceId,
        WorkspaceIsolationBinding binding,
        NetworkConnectionProfile connection,
        CancellationToken cancellationToken,
        SecretMaterial? transientPassword = null)
    {
        Validate(workspaceId, binding);
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.Configuration is NetworkConnectionConfiguration.Proxy proxy)
        {
            return await ArmProxyAsync(
                    binding,
                    connection.Id,
                    proxy,
                    transientPassword,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await RunArmAsync(
            binding,
            ArmScript,
            [WorkspaceIsolationNetworkNames.TunnelInterface(connection.Id)],
            "workspace_network_kill_switch_arm_failed",
            "The workspace network kill switch could not be enabled inside the isolate.",
            cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask<NetworkConnectionResult<Unit>> DisarmAsync(
        WorkspaceInstanceId workspaceId,
        WorkspaceIsolationBinding binding,
        CancellationToken cancellationToken)
    {
        Validate(workspaceId, binding);
        return RunAsync(
            binding,
            DisarmScript,
            [],
            "workspace_network_kill_switch_disarm_failed",
            "The workspace network kill switch could not restore direct isolate egress.",
            cancellationToken);
    }

    private async ValueTask<WorkspaceIsolationEgressGuardArmResult> ArmProxyAsync(
        WorkspaceIsolationBinding binding,
        NetworkConnectionId connectionId,
        NetworkConnectionConfiguration.Proxy proxy,
        SecretMaterial? transientPassword,
        CancellationToken cancellationToken)
    {
        if (proxy.Protocol == NetworkProxyProtocol.Https
            && Uri.CheckHostName(proxy.Host) is not (UriHostNameType.Dns or UriHostNameType.IPv4))
        {
            return ProxyFailure(
                NetworkConnectionErrorCode.InvalidConfiguration,
                "workspace_proxy_tls_host_invalid",
                "An HTTPS proxy used by an isolated workspace must have a DNS name or IPv4 address.",
                retryable: false,
                isEnforced: false);
        }

        var password = await ResolveProxyPasswordAsync(
                connectionId,
                proxy.PasswordSecret,
                transientPassword,
                cancellationToken)
            .ConfigureAwait(false);
        if (password is NetworkConnectionResult<byte[]>.Failure secretFailure)
        {
            return WorkspaceIsolationEgressGuardArmResult.Failed(
                secretFailure.Error,
                isEnforced: false);
        }

        var passwordBytes = ((NetworkConnectionResult<byte[]>.Success)password).Value;
        byte[]? configuration = null;
        try
        {
            configuration = BuildRedsocksConfiguration(proxy, passwordBytes);
            return await RunProxyArmAsync(
                    binding,
                    proxy,
                    configuration,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DecoderFallbackException)
        {
            return ProxyFailure(
                NetworkConnectionErrorCode.InvalidConfiguration,
                "proxy_secret_invalid_encoding",
                "The proxy password must be valid UTF-8 text.",
                retryable: false,
                isEnforced: false);
        }
        catch (InvalidDataException)
        {
            return ProxyFailure(
                NetworkConnectionErrorCode.InvalidConfiguration,
                "proxy_secret_invalid_text",
                "The proxy password cannot contain control characters.",
                retryable: false,
                isEnforced: false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            if (configuration is not null)
            {
                CryptographicOperations.ZeroMemory(configuration);
            }
        }
    }

    private async ValueTask<NetworkConnectionResult<byte[]>> ResolveProxyPasswordAsync(
        NetworkConnectionId connectionId,
        SecretRef? passwordReference,
        SecretMaterial? transientPassword,
        CancellationToken cancellationToken)
    {
        if (passwordReference is null)
        {
            if (transientPassword is null)
            {
                return NetworkConnectionResult<byte[]>.Succeed([]);
            }

            var transientBytes = GC.AllocateUninitializedArray<byte>(transientPassword.Length);
            transientPassword.CopyTo(transientBytes);
            return NetworkConnectionResult<byte[]>.Succeed(transientBytes);
        }

        if (_secretVault is null)
        {
            return NetworkConnectionResult<byte[]>.Fail(new NetworkConnectionError(
                NetworkConnectionErrorCode.InvalidConfiguration,
                "proxy_secret_unavailable",
                "The proxy credential is unavailable.",
                retryable: false));
        }

        SecretVaultResult<SecretMaterial> resolved;
        try
        {
            resolved = await _secretVault.ResolveAsync(
                    new ResolveSecretRequest(
                        passwordReference.Value,
                        new SecretScope(
                            SecretScopeKind.NetworkConnection,
                            connectionId.Value),
                        new SecretUsePurpose(
                            SecretUseKind.NetworkConnectionAuthentication,
                            connectionId.Value)),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return NetworkConnectionResult<byte[]>.Fail(new NetworkConnectionError(
                NetworkConnectionErrorCode.Cancelled,
                "proxy_secret_cancelled",
                "Proxy credential access was cancelled.",
                retryable: false));
        }

        if (resolved is SecretVaultResult<SecretMaterial>.Failure failure)
        {
            var authenticationRequired = failure.Error.Code is
                SecretVaultErrorCode.AuthenticationRequired or SecretVaultErrorCode.UserCancelled;
            return NetworkConnectionResult<byte[]>.Fail(new NetworkConnectionError(
                authenticationRequired
                    ? NetworkConnectionErrorCode.AuthenticationRequired
                    : NetworkConnectionErrorCode.InvalidConfiguration,
                "proxy_secret_unavailable",
                authenticationRequired
                    ? "Authentication is required to access the proxy credential."
                    : "The proxy credential is unavailable.",
                failure.Error.Retryable || authenticationRequired));
        }

        using var material = ((SecretVaultResult<SecretMaterial>.Success)resolved).Value;
        var bytes = new byte[material.Length];
        material.CopyTo(bytes);
        return NetworkConnectionResult<byte[]>.Succeed(bytes);
    }

    private async ValueTask<WorkspaceIsolationEgressGuardArmResult> RunProxyArmAsync(
        WorkspaceIsolationBinding binding,
        NetworkConnectionConfiguration.Proxy proxy,
        ReadOnlyMemory<byte> configuration,
        CancellationToken cancellationToken)
    {
        var protocol = proxy.Protocol switch
        {
            NetworkProxyProtocol.Socks5 => "socks5",
            NetworkProxyProtocol.Http => "http",
            NetworkProxyProtocol.Https => "https",
            _ => throw new ArgumentOutOfRangeException(nameof(proxy)),
        };
        var launch = _isolationProvider.CreateExecLaunch(
            binding,
            new WorkspaceIsolationProcessRequest(
                ConnectionKind.Local,
                "/bin/sh",
                [
                    "-c",
                    ProxyArmScript,
                    "ghostshell-network-guard",
                    protocol,
                    proxy.Host,
                    proxy.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ]));
        if (launch is WorkspaceIsolationResult<WorkspaceProcessLaunch>.Failure launchFailure)
        {
            return ProxyFailure(
                NetworkConnectionErrorCode.RouteUnavailable,
                "workspace_proxy_route_setup_failed",
                launchFailure.Error.Message,
                launchFailure.Error.Retryable,
                isEnforced: false);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(OperationTimeout);
        try
        {
            var result = await _commandRunner.RunAsync(
                    ((WorkspaceIsolationResult<WorkspaceProcessLaunch>.Success)launch).Value,
                    configuration,
                    deadline.Token)
                .ConfigureAwait(false);
            return result.ExitCode switch
            {
                0 => WorkspaceIsolationEgressGuardArmResult.Enforced(),
                69 => ProxyFailure(
                    NetworkConnectionErrorCode.RuntimeMissing,
                    "workspace_proxy_runtime_missing",
                    proxy.Protocol == NetworkProxyProtocol.Https
                        ? "The workspace image needs iproute2, nftables, procps, redsocks, socat, and ca-certificates to enforce this HTTPS proxy."
                        : "The workspace image needs iproute2, nftables, procps, and redsocks to enforce this proxy.",
                    retryable: false,
                    isEnforced: false),
                77 => ProxyFailure(
                    NetworkConnectionErrorCode.RouteUnavailable,
                    "workspace_network_guard_privileges_missing",
                    "The workspace user cannot administer the isolated network boundary required by the proxy route.",
                    retryable: false,
                    isEnforced: false),
                168 => ProxyFailure(
                    NetworkConnectionErrorCode.ConnectionFailed,
                    "workspace_proxy_host_unresolved",
                    "The isolated workspace could not resolve the proxy server to an IPv4 address. Direct egress remains blocked.",
                    retryable: true,
                    isEnforced: true),
                171 => ProxyFailure(
                    NetworkConnectionErrorCode.ConnectionFailed,
                    "workspace_proxy_sidecar_start_failed",
                    "The isolated workspace could not start its transparent proxy service. Direct egress remains blocked.",
                    retryable: true,
                    isEnforced: true),
                172 => ProxyFailure(
                    NetworkConnectionErrorCode.RouteUnavailable,
                    "workspace_proxy_dns_setup_failed",
                    "The isolated workspace could not configure DNS through the proxy. Direct egress remains blocked.",
                    retryable: true,
                    isEnforced: true),
                173 => ProxyFailure(
                    NetworkConnectionErrorCode.ConnectionFailed,
                    "workspace_proxy_dns_probe_failed",
                    "DNS could not pass through the proxy. The proxy must allow TCP connections to a DNS resolver on port 53. Direct egress remains blocked.",
                    retryable: true,
                    isEnforced: true),
                _ => ProxyFailure(
                    NetworkConnectionErrorCode.RouteUnavailable,
                    "workspace_proxy_route_setup_failed",
                    "The isolated workspace could not enforce the proxy route. Direct egress remains blocked.",
                    retryable: true,
                    isEnforced: result.ExitCode == 78),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ProxyFailure(
                NetworkConnectionErrorCode.Cancelled,
                "workspace_proxy_route_cancelled",
                "Enabling the isolated workspace proxy was cancelled after launch. Direct egress is treated as blocked because the guard state could not be confirmed.",
                retryable: false,
                isEnforced: true);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            return ProxyFailure(
                NetworkConnectionErrorCode.RouteUnavailable,
                "workspace_proxy_route_setup_failed",
                "The isolated workspace proxy did not finish starting. Direct egress is treated as blocked because the guard state could not be confirmed.",
                retryable: true,
                isEnforced: true);
        }
    }

    private static byte[] BuildRedsocksConfiguration(
        NetworkConnectionConfiguration.Proxy proxy,
        ReadOnlySpan<byte> password)
    {
        _ = StrictUtf8.GetCharCount(password);
        if (password.ContainsAnyInRange((byte)0, (byte)31) || password.Contains((byte)127))
        {
            throw new InvalidDataException("Proxy passwords cannot contain control characters.");
        }

        var targetHost = proxy.Protocol == NetworkProxyProtocol.Https
            ? "127.0.0.1"
            : "GHOSTSHELL_PROXY_IP";
        var targetPort = proxy.Protocol == NetworkProxyProtocol.Https ? 10081 : proxy.Port;
        var proxyType = proxy.Protocol == NetworkProxyProtocol.Socks5
            ? "socks5"
            : "http-connect";
        using var stream = new MemoryStream();
        WriteUtf8(stream, "base {\n  log_debug = off;\n  log_info = off;\n  log = \"stderr\";\n  daemon = on;\n  redirector = iptables;\n}\nredsocks {\n  local_ip = 127.0.0.1;\n  local_port = 10080;\n  ip = ");
        WriteUtf8(stream, targetHost);
        WriteUtf8(stream, ";\n  port = ");
        WriteUtf8(stream, targetPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        WriteUtf8(stream, ";\n  type = ");
        WriteUtf8(stream, proxyType);
        WriteUtf8(stream, ";\n");
        if (proxy.Username is not null)
        {
            WriteUtf8(stream, "  login = \"");
            WriteEscaped(stream, Encoding.UTF8.GetBytes(proxy.Username));
            WriteUtf8(stream, "\";\n  password = \"");
            WriteEscaped(stream, password);
            WriteUtf8(stream, "\";\n");
        }

        WriteUtf8(stream, "}\ndnstc {\n  local_ip = 127.0.0.1;\n  local_port = 10053;\n}\n");
        var result = stream.ToArray();
        if (stream.TryGetBuffer(out var buffer))
        {
            CryptographicOperations.ZeroMemory(buffer.AsSpan());
        }

        return result;
    }

    private static void WriteEscaped(Stream stream, ReadOnlySpan<byte> value)
    {
        foreach (var character in value)
        {
            if (character is (byte)'\\' or (byte)'\"')
            {
                stream.WriteByte((byte)'\\');
            }

            stream.WriteByte(character);
        }
    }

    private static void WriteUtf8(Stream stream, string value) =>
        stream.Write(Encoding.UTF8.GetBytes(value));

    private static WorkspaceIsolationEgressGuardArmResult ProxyFailure(
        NetworkConnectionErrorCode code,
        string stableCode,
        string message,
        bool retryable,
        bool isEnforced) =>
        WorkspaceIsolationEgressGuardArmResult.Failed(
            new NetworkConnectionError(code, stableCode, message, retryable),
            isEnforced);

    private async ValueTask<NetworkConnectionResult<Unit>> RunAsync(
        WorkspaceIsolationBinding binding,
        string script,
        IReadOnlyList<string> arguments,
        string stableCode,
        string message,
        CancellationToken cancellationToken)
    {
        var launch = _isolationProvider.CreateExecLaunch(
            binding,
            new WorkspaceIsolationProcessRequest(
                ConnectionKind.Local,
                "/bin/sh",
                ["-c", script, "ghostshell-network-guard", .. arguments]));
        if (launch is WorkspaceIsolationResult<WorkspaceProcessLaunch>.Failure launchFailure)
        {
            return NetworkConnectionResult<Unit>.Fail(new NetworkConnectionError(
                NetworkConnectionErrorCode.RouteUnavailable,
                stableCode,
                launchFailure.Error.Message,
                launchFailure.Error.Retryable));
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(OperationTimeout);
        try
        {
            var result = await _commandRunner.RunAsync(
                    ((WorkspaceIsolationResult<WorkspaceProcessLaunch>.Success)launch).Value,
                    ReadOnlyMemory<byte>.Empty,
                    deadline.Token)
                .ConfigureAwait(false);
            if (result.ExitCode == 0)
            {
                return NetworkConnectionResult<Unit>.Succeed(Unit.Value);
            }

            var runtimeMissing = result.ExitCode == 69;
            return NetworkConnectionResult<Unit>.Fail(new NetworkConnectionError(
                runtimeMissing
                    ? NetworkConnectionErrorCode.RuntimeMissing
                    : NetworkConnectionErrorCode.RouteUnavailable,
                runtimeMissing ? "workspace_network_guard_runtime_missing" : stableCode,
                runtimeMissing
                    ? "The isolated workspace needs iproute2, nftables, and procps to enforce its network kill switch."
                    : message,
                retryable: !runtimeMissing));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return NetworkConnectionResult<Unit>.Fail(new NetworkConnectionError(
                NetworkConnectionErrorCode.Cancelled,
                "workspace_network_kill_switch_cancelled",
                "Changing the workspace network kill switch was cancelled.",
                retryable: false));
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            return NetworkConnectionResult<Unit>.Fail(new NetworkConnectionError(
                NetworkConnectionErrorCode.RouteUnavailable,
                stableCode,
                message,
                retryable: true));
        }
    }

    private async ValueTask<WorkspaceIsolationEgressGuardArmResult> RunArmAsync(
        WorkspaceIsolationBinding binding,
        string script,
        IReadOnlyList<string> arguments,
        string stableCode,
        string message,
        CancellationToken cancellationToken)
    {
        var launch = _isolationProvider.CreateExecLaunch(
            binding,
            new WorkspaceIsolationProcessRequest(
                ConnectionKind.Local,
                "/bin/sh",
                ["-c", script, "ghostshell-network-guard", .. arguments]));
        if (launch is WorkspaceIsolationResult<WorkspaceProcessLaunch>.Failure launchFailure)
        {
            return WorkspaceIsolationEgressGuardArmResult.Failed(
                new NetworkConnectionError(
                    NetworkConnectionErrorCode.RouteUnavailable,
                    stableCode,
                    launchFailure.Error.Message,
                    launchFailure.Error.Retryable),
                isEnforced: false);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(OperationTimeout);
        try
        {
            var result = await _commandRunner.RunAsync(
                    ((WorkspaceIsolationResult<WorkspaceProcessLaunch>.Success)launch).Value,
                    ReadOnlyMemory<byte>.Empty,
                    deadline.Token)
                .ConfigureAwait(false);
            if (result.ExitCode == 0)
            {
                return WorkspaceIsolationEgressGuardArmResult.Enforced();
            }

            var runtimeMissing = result.ExitCode == 69;
            var privilegesMissing = result.ExitCode == 77;
            var error = new NetworkConnectionError(
                runtimeMissing
                    ? NetworkConnectionErrorCode.RuntimeMissing
                    : NetworkConnectionErrorCode.RouteUnavailable,
                runtimeMissing
                    ? "workspace_network_guard_runtime_missing"
                    : privilegesMissing
                        ? "workspace_network_guard_privileges_missing"
                        : stableCode,
                runtimeMissing
                    ? "The isolated workspace needs iproute2, nftables, and procps to enforce its network kill switch."
                    : privilegesMissing
                        ? "The workspace user cannot administer the isolated network namespace required by the kill switch."
                        : message,
                retryable: !runtimeMissing && !privilegesMissing);
            return WorkspaceIsolationEgressGuardArmResult.Failed(
                error,
                isEnforced: result.ExitCode == 78);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return WorkspaceIsolationEgressGuardArmResult.Failed(
                new NetworkConnectionError(
                    NetworkConnectionErrorCode.Cancelled,
                    "workspace_network_kill_switch_cancelled",
                    "Changing the workspace network kill switch was cancelled after launch. Direct egress is treated as blocked because the guard state could not be confirmed.",
                    retryable: false),
                isEnforced: true);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            return WorkspaceIsolationEgressGuardArmResult.Failed(
                new NetworkConnectionError(
                    NetworkConnectionErrorCode.RouteUnavailable,
                    stableCode,
                    $"{message} Direct egress is treated as blocked because the guard state could not be confirmed.",
                    retryable: true),
                isEnforced: true);
        }
    }

    private static void Validate(
        WorkspaceInstanceId workspaceId,
        WorkspaceIsolationBinding binding)
    {
        if (string.IsNullOrWhiteSpace(workspaceId.Value))
        {
            throw new ArgumentException("A workspace instance ID is required.", nameof(workspaceId));
        }

        ArgumentNullException.ThrowIfNull(binding);
        const WorkspaceIsolationCapability required =
            WorkspaceIsolationCapability.DedicatedNetworkNamespace
            | WorkspaceIsolationCapability.StructuredProcessExecution;
        if ((binding.Capabilities & required) != required)
        {
            throw new ArgumentException(
                "The workspace binding cannot enforce isolated network egress.",
                nameof(binding));
        }
    }
}
