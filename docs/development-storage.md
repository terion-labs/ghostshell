# Development and installed application storage

Ordinary builds, including `dotnet run --configuration Release`, use a separate
development namespace. They never import the installed application's database,
browser archives, or keys. This intentionally starts development with a fresh
profile.

| Storage | Installed app | Development |
| --- | --- | --- |
| macOS Application Support, Caches, Logs directory | `Asura` | `Asura Development` |
| Windows local application directory | `Asura` | `Asura Development` |
| Linux XDG application directory | `asura` | `asura-development` |
| OS vault service | `sh.asura` | `sh.asura.development` |
| Temporary browser working-tree parent | `Asura` | `Asura Development` |
| macOS bundle identifier | `sh.asura` | `sh.asura.development` |

Database, startup protection, browser archives, VPN state, SDK workspace disks,
and single-instance coordination inherit the data root. Encryption and stored
connection credentials inherit the vault service. Account names can stay the
same because the vault service distinguishes their owners.

`scripts/package-macos.sh` explicitly passes `AsuraProductionBuild=true`
to both managed-evidence and Native AOT publishing. Do not pass this property
to development builds or tests. Production retains its existing paths and keys.

Release rehearsal uses a temporary signing keychain, passed explicitly to signing
and notarization. It must never become the user's default keychain. Otherwise,
an unrelated app can create a key in that temporary keychain and lose it when
the rehearsal deletes the keychain.
