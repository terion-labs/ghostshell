<script setup lang="ts">
const base = useRuntimeConfig().app.baseURL

const siteUrl = 'https://asura.sh'

useHead({ link: [{ rel: 'canonical', href: `${siteUrl}/` }] })

useSeoMeta({
  title: 'Asura — your terminal workspace',
  description:
    'A native terminal workspace with an AI agent that operates local and remote machines over plain SSH. Terminal, browser, files, databases, Docker, Git, and monitoring in one window, each workspace in its own VM and on its own VPN. Nothing to install on your servers.',
  ogTitle: 'Asura — your terminal workspace',
  ogDescription:
    'A native terminal workspace with an AI agent that operates local and remote machines over plain SSH. Nothing to install on your servers.',
  ogType: 'website',
  ogUrl: `${siteUrl}/`,
  ogSiteName: 'Asura',
  ogImage: `${siteUrl}/share.png`,
  ogImageWidth: 1731,
  ogImageHeight: 909,
  ogImageType: 'image/png',
  ogImageAlt:
    'Asura logo: AI-native terminal workspaces and multitool',
  twitterCard: 'summary_large_image',
  twitterTitle: 'Asura — your terminal workspace',
  twitterDescription:
    'A native terminal workspace with an AI agent that operates local and remote machines over plain SSH.',
  twitterImage: `${siteUrl}/share.png`,
})

const providers = [
  'Anthropic Claude',
  'OpenAI',
  'Google Gemini',
  'xAI Grok',
  'DeepSeek',
  'Moonshot AI',
  'OpenRouter',
  'GitHub Copilot',
  'Amazon Bedrock',
  'Ollama',
  'OpenAI-compatible endpoints',
]

const faqs = [
  {
    q: 'Do I have to install anything on my servers?',
    a: 'No. Everything remote runs over standard SSH and SFTP. Session continuity uses the tmux or GNU Screen already on the box, monitoring samples with plain system commands, and the agent drives a normal PTY. No daemon, no sidecar, no extra open ports.',
  },
  {
    q: 'How does the browser reach a remote localhost?',
    a: 'When you bind a browser panel to an SSH connection, Asura routes that panel through the existing SSH session. Requests to localhost or private subnet addresses resolve on the remote side, so a dev server on the remote machine loads like a local page. No VPN, no manual ssh -L, no proxy config files.',
  },
  {
    q: 'Is this an Electron app?',
    a: 'No. Asura is a native desktop application. The terminal runs on libghostty-vt, the C engine from the Ghostty project, so it feels like Ghostty, not like a web page pretending to be a terminal. The only web engine in the app is the Chromium behind the browser panels, and it never touches the terminal.',
  },
  {
    q: 'Can the AI agent see my passwords or keys?',
    a: 'No. Secrets live in your OS vault (macOS Keychain, Windows credential store, Linux Secret Service) and the app passes around opaque references to them. The agent works with session handles, not credentials. When a subprocess needs a passphrase, it arrives through a single-use, current-user-only pipe and is wiped from memory after use.',
  },
  {
    q: 'What does the agent actually control?',
    a: 'Whatever you approve. Every mutating action needs a one-click approval, or an explicit time-bounded run window you grant for terminal actions. If you start typing in the terminal yourself, the agent loses its input lease immediately. You can watch every keystroke it sends. In the browser it works from accessibility snapshots and element references, page content is treated as untrusted, and every mutation goes through the same approval.',
  },
  {
    q: 'What platforms are supported?',
    a: 'macOS, Windows, and Linux from one codebase. The current early release ships a signed, notarized macOS Apple-silicon build; on other platforms you build from source.',
  },
  {
    q: 'Does a workspace VPN affect the rest of my machine?',
    a: 'No. Each connection runs as a userspace tunnel inside Asura: no system VPN profile, no kernel extension, no change to your routing table. Only the workspaces that chose that connection send traffic through it, and different workspaces can be on different tunnels at the same time. Your browser, mail, and everything else on the machine keep using the network as before.',
  },
  {
    q: 'What happens when a VPN drops?',
    a: 'That is what the kill switch is for. With it on, a workspace whose route fails stops sending traffic until the route is back or you turn networking off for that workspace. It never quietly falls back to your direct connection. Inside an isolated workspace this is enforced by the VM itself, which has no other way out; in a non-isolated workspace it covers everything Asura routes, while a stray program that ignores proxy settings could still reach the network directly.',
  },
  {
    q: 'What does workspace isolation actually isolate?',
    a: 'Each isolated workspace is a persistent Linux container with its own kernel, root filesystem, and network namespace. On macOS it runs on Apple Containerization, where every container is its own lightweight VM. Local terminals, the file panel, and browser traffic run inside it, and you can run the agent there too. The host stays invisible except for the folders you explicitly mount. It needs Apple silicon, macOS 26, and Apple\'s container runtime, which the app offers to install for you.',
  },
]

const open = ref<number | null>(0)
</script>

<template>
  <div>
    <div class="notice">
      <div class="wrap notice__row">
        <span class="notice__badge">Alpha</span>
        <span>
          Asura is in early alpha and currently macOS only.
          Windows and Linux are on the way.
        </span>
      </div>
    </div>

    <SiteHeader />

    <main>
      <!-- Hero -->
      <section class="section section--flush hero">
        <div class="wrap">
          <div class="hero__inner">
            <p class="eyebrow">The AI-native terminal workspace</p>
            <h1 class="hero__title">Your terminal workspace</h1>
            <p class="hero__lede">
              Asura is a native terminal workspace with an AI agent that
              operates your machines, local and remote, over plain SSH.
              Terminal, browser, files, databases, Docker, Git, and monitoring
              in one window. Nothing to install on your servers.
            </p>
            <div class="hero__ctas">
              <a class="btn btn--primary" href="#download">
                Download for macOS
                <span class="btn__sub">Apple silicon</span>
              </a>
              <a class="btn" href="https://github.com/terion-labs/asura">
                View on GitHub
              </a>
            </div>
            <ul class="hero__badges">
              <li>Ghostty terminal engine</li>
              <li>Embedded browser and dev tools</li>
              <li>Fully AI-agent controlled</li>
              <li>A VPN per workspace</li>
              <li>Encrypted and secure</li>
            </ul>
          </div>
          <div class="shot hero__shot">
            <img
              :src="`${base}shots/workspace-agent.webp`"
              alt="Asura workspace: an SSH terminal, a browser panel, and the AI agent fixing a failed deploy"
              width="2880"
              height="1800"
              fetchpriority="high"
            />
          </div>
        </div>
      </section>

      <!-- The four things -->
      <section id="different" class="section">
        <div class="wrap">
          <p class="eyebrow">Why it exists</p>
          <h2 data-reveal class="section-title">Four things you won't find in your current terminal</h2>
          <div class="grid quad">
            <div class="card" data-reveal>
              <h3>Browse a remote's localhost</h3>
              <p>
                Your dev server runs on a cloud box at
                <code>localhost:3000</code>. Open a browser panel, pick that
                SSH connection, type the address. Asura routes the panel
                through the tunnel and the page loads, private subnet
                dashboards included. No VPN, no <code>ssh -L</code> ritual.
                And the agent can browse those pages too.
              </p>
            </div>
            <div class="card" data-reveal>
              <h3>An agent with zero footprint</h3>
              <p>
                The AI agent runs inside the app on your machine and reaches
                servers over the SSH session you already have. It reads the
                terminal, types, handles interactive prompts and TUIs. The
                remote host sees an ordinary SSH user, because that is all
                there is.
              </p>
            </div>
            <div class="card" data-reveal>
              <h3>Every panel goes remote</h3>
              <p>
                Files, databases, Docker, processes, system stats: each panel
                has a host dropdown. Flip it from Local to a connection and the
                same panel works against the remote machine, over SSH, without
                a remote agent. One app instead of seven.
              </p>
            </div>
            <div class="card" data-reveal>
              <h3>A workspace in its own VM, on its own VPN</h3>
              <p>
                Flip a switch and the whole workspace runs in a persistent,
                lightweight Linux VM: own kernel, own filesystem, own
                network. Give it its own tunnel too: the corporate
                AnyConnect, your homelab's Tailscale, a WireGuard peer.
                Everything inside goes out that way and nothing else on
                your machine does. macOS today; Linux and Windows planned.
              </p>
            </div>
          </div>
        </div>
      </section>

      <!-- Agent -->
      <section id="agent" class="section section--alt">
        <div class="wrap split">
          <div class="split__text" data-reveal>
            <p class="eyebrow">The agent</p>
            <h2 data-reveal class="section-title">An agent that works where you work</h2>
            <p data-reveal style="--rd: 1" class="section-lede">
              Ask it to fix a failed deploy and it reads the logs, checks the
              service, and proposes the command. It runs in-process, native
              .NET, no Node sidecar on your laptop and nothing at all on the
              server.
            </p>
            <ul class="checks">
              <li>
                Types like you do: real keystrokes in real terminals, so it
                can drive <code>vim</code>, installers, and cloud CLI
                wizards, not just run commands.
              </li>
              <li>
                Drives the browser too. It reads the page as an
                accessibility snapshot and clicks, fills, and checks
                elements by reference, never blind coordinates. Point the
                browser through an SSH tunnel and that includes your remote
                internal web apps.
              </li>
              <li>
                Nothing happens without you. Every change waits for your
                one-click approval, or a run window you grant on your terms.
              </li>
              <li>
                Touch the keyboard and the agent steps aside instantly. You
                always win.
              </li>
              <li>
                It never sees your secrets. Keys and passwords stay locked
                in your OS vault.
              </li>
              <li>
                Searches the web when a task needs fresh information, and
                plugs into your own tools over MCP. Same approvals on every
                call.
              </li>
            </ul>
            <div class="providers">
              <p class="providers__label">Bring your own model</p>
              <ul class="providers__list">
                <li v-for="p in providers" :key="p">{{ p }}</li>
              </ul>
            </div>
          </div>
          <div class="shot split__shot" data-reveal style="--rd: 1">
            <img
              :src="`${base}shots/workspace-agent-reasoning.webp`"
              alt="The docked agent streaming its reasoning while a tool call waits for approval"
              width="2880"
              height="1800"
              loading="lazy"
            />
          </div>
        </div>
      </section>

      <!-- Panels tour -->
      <section id="panels" class="section">
        <div class="wrap">
          <p class="eyebrow">One window</p>
          <h2 data-reveal class="section-title">The panels</h2>
          <p data-reveal style="--rd: 1" class="section-lede">
            Split them, stack them, save the layout as a screen you can reopen
            with one click. Every panel picks its own host.
          </p>
          <PanelTour class="panels-tour" data-reveal style="--rd: 1" />
        </div>
      </section>

      <!-- Sessions -->
      <section class="section section--alt">
        <div class="wrap split split--flip">
          <div class="split__text" data-reveal>
            <p class="eyebrow">Session continuity</p>
            <h2 data-reveal class="section-title">Close the lid. The build keeps running.</h2>
            <p data-reveal style="--rd: 1" class="section-lede">
              Remote terminals keep their own session running on the server,
              isolated from anything else you have there. Drop Wi-Fi, switch
              networks, restart the app: the compile keeps going and the
              terminal reattaches when you return. It rides on the
              <code>tmux</code> or GNU Screen already on the box, so there is
              still nothing to install.
            </p>
            <p data-reveal style="--rd: 1" class="section-lede">
              There is also a Quick Terminal: a global hotkey drops a terminal
              over whatever you are doing, on all three platforms.
            </p>
          </div>
          <div class="shot split__shot sessions__shot" data-reveal style="--rd: 1">
            <img
              :src="`${base}shots/workspace.webp`"
              alt="A workspace with an SSH terminal and browser panel side by side"
              width="2880"
              height="1800"
              loading="lazy"
            />
            <div class="sessions__glow" aria-hidden="true">
              <svg viewBox="0 0 16 16">
                <path d="M5.1 3a.5.5 0 0 0-.43.25l-2.6 4.5a.5.5 0 0 0 0 .5l2.5 4.34a.83.83 0 0 0 1.5-.17l2.9-9.14a1.83 1.83 0 0 1 3.32-.37l2.5 4.34a1.5 1.5 0 0 1 0 1.5l-2.6 4.5a1.5 1.5 0 0 1-1.3.75H8.5a.5.5 0 0 1 0-1h2.4a.5.5 0 0 0 .43-.25l2.6-4.5a.5.5 0 0 0 0-.5l-2.5-4.34a.83.83 0 0 0-1.5.17l-2.9 9.14a1.83 1.83 0 0 1-3.32.37L1.2 8.75a1.5 1.5 0 0 1 0-1.5l2.6-4.5A1.5 1.5 0 0 1 5.1 2h2.4a.5.5 0 0 1 0 1z" />
              </svg>
            </div>
          </div>
        </div>
      </section>

      <!-- Workspaces -->
      <section id="workspaces" class="section">
        <div class="wrap split">
          <div class="split__text" data-reveal>
            <p class="eyebrow">Workspaces</p>
            <h2 data-reveal class="section-title">Prod, staging, and personal never meet</h2>
            <p data-reveal style="--rd: 1" class="section-lede">
              A workspace holds its own connections, tabs, layouts, and
              browser identity. The embedded browser keeps cookies and
              sessions per workspace, so the AWS account you are logged into
              in <em>Client A</em> does not exist in <em>Client B</em>.
            </p>
            <ul class="checks">
              <li>
                Isolated browser profiles per workspace: separate logins,
                separate sessions, zero cookie bleed between clients or
                between work and personal.
              </li>
              <li>
                Saved screens: a four-panel layout of terminal, browser,
                database, and monitor reopens exactly as you left it.
              </li>
              <li>
                Workspace-scoped agent: the agent only sees the panels of the
                workspace it lives in.
              </li>
            </ul>
          </div>
          <div class="shot split__shot" data-reveal style="--rd: 1">
            <img
              :src="`${base}shots/settings-workspaces.webp`"
              alt="Workspace settings listing separate workspaces with their own connections"
              width="2880"
              height="1800"
              loading="lazy"
            />
          </div>
        </div>
      </section>

      <!-- Workspace isolation -->
      <section id="isolation" class="section">
        <div class="wrap split split--flip">
          <div class="split__text" data-reveal>
            <p class="eyebrow">Workspace isolation</p>
            <h2 data-reveal class="section-title">The whole workspace in its own VM</h2>
            <p data-reveal style="--rd: 1" class="section-lede">
              Flip one switch and a workspace runs inside its own persistent
              Linux environment: a lightweight VM with its own kernel,
              filesystem, and network namespace. Its terminals, file panel,
              and browser traffic live in there, and the agent can too.
              Install whatever the project needs. The host stays clean.
            </p>
            <ul class="checks">
              <li>
                Persistent by design. Closing the workspace stops the VM,
                not its disk. Run <code>apt install</code> once and it is
                still there next week.
              </li>
              <li>
                The host stays private. Nothing is shared unless you mount
                it: pick host folders per workspace, read-only or
                read-write, or mount nothing at all.
              </li>
              <li>
                Conflicting stacks stop conflicting. One project on Node 18
                and Postgres 14, another on Node 22 and Postgres 17, side
                by side, no version managers.
              </li>
              <li>
                Bring your own image. Ubuntu 24.04 by default, or any
                bootable OCI image with an init.
              </li>
              <li>
                The browser follows the workspace: browser panels route
                through the isolate's network, so they reach exactly what
                the workspace can reach.
              </li>
              <li>
                Give it a network of its own. Pair isolation with a
                <a href="#networking">per-workspace VPN or proxy</a> and the
                VM's only way out is the route you chose, so every process
                inside obeys it, not only the ones that honour proxy
                settings.
              </li>
              <li>
                macOS on Apple silicon today, on Apple's container runtime
                (every container is its own lightweight VM). Linux and
                Windows backends are planned.
              </li>
            </ul>
          </div>
          <div class="shot split__shot" data-reveal style="--rd: 1">
            <img
              :src="`${base}shots/workspace-isolation.webp`"
              alt="Workspace settings with the Isolate workspace switch and host mount options"
              width="1760"
              height="1100"
              loading="lazy"
            />
          </div>
        </div>
      </section>

      <!-- Per-workspace networking -->
      <section id="networking" class="section section--alt">
        <div class="wrap split">
          <div class="split__text" data-reveal>
            <p class="eyebrow">Per-workspace networking</p>
            <h2 data-reveal class="section-title">Every workspace on its own VPN</h2>
            <p data-reveal style="--rd: 1" class="section-lede">
              The client workspace on the corporate AnyConnect. The homelab
              workspace on your Tailscale. The personal one wrapped in
              WireGuard. All at the same time, in one app, and none of it
              touching the rest of your machine.
            </p>
            <ul class="checks">
              <li>
                Five kinds of route: SOCKS5, HTTP, or HTTPS proxy,
                WireGuard, OpenVPN, Cisco AnyConnect, and a Tailscale exit
                node. The engines ship inside the app. No VPN client to
                install, no kernel extension, no admin prompt.
              </li>
              <li>
                Nothing system-wide. Each connection is a userspace tunnel
                the app owns: no system VPN, no change to your routing
                table, no other program's traffic dragged along with it.
              </li>
              <li>
                The whole workspace goes through it. Terminals and SSH,
                browser panels, files, databases, Docker, Git, MCP servers,
                and the agent if you run it in there. DNS too, so names
                resolve inside the tunnel instead of at your ISP.
              </li>
              <li>
                One switch in the title bar. Set an app-wide default, give a
                workspace its own list of allowed connections, then swap or
                pause the route from the window without editing anything.
              </li>
              <li>
                A kill switch. If the tunnel drops, the workspace's traffic
                stops rather than sliding back onto your direct connection.
              </li>
              <li>
                Made for isolation. An isolated workspace's VM has exactly
                one way out, the route you picked, so every process in it
                obeys, not only the ones that honour proxy settings.
              </li>
              <li>
                Configs, keys, and passwords live in the OS vault, never in
                the connection definition. A password you would rather not
                store is asked for at connect time and forgotten afterwards.
              </li>
            </ul>
          </div>
          <div class="shot split__shot" data-reveal style="--rd: 1">
            <img
              :src="`${base}shots/settings-networking.webp`"
              alt="Networking settings: the application default route and a list of saved AnyConnect, proxy, Tailscale, WireGuard, and OpenVPN connections"
              width="2880"
              height="1800"
              loading="lazy"
            />
          </div>
        </div>
      </section>

      <!-- Security -->
      <section id="security" class="section">
        <div class="wrap">
          <p class="eyebrow">Security</p>
          <h2 data-reveal class="section-title">Built like it expects to be audited</h2>
          <p data-reveal style="--rd: 1" class="section-lede">
            The security model is not a settings page. It is the architecture.
          </p>
          <div class="grid trio">
            <div class="card" data-reveal>
              <h3>Everything encrypted</h3>
              <p>
                Every record the app writes to disk, from connection profiles
                to session history, is encrypted with 256-bit AES. Keys
                derive from your passcode through 600,000 rounds of
                hardening, and guessing attempts hit a backoff that survives
                restarts.
              </p>
            </div>
            <div class="card" data-reveal>
              <h3>Secrets stay in your OS vault</h3>
              <p>
                SSH passphrases, API keys, and database passwords live in the
                macOS Keychain, the Windows credential store, or the Linux
                Secret Service, never in config files. The app passes around
                references, hands the real value over a single-use channel
                only at the moment of use, and wipes it from memory after.
              </p>
            </div>
            <div class="card" data-reveal>
              <h3>Locked behind your fingerprint</h3>
              <p>
                Open the app with Touch ID, Windows Hello, or a PIN. Paste
                with escape sequences in it? The terminal stops and asks
                first. A remote process reading your clipboard? Also asks
                first.
              </p>
            </div>
          </div>
        </div>
      </section>

      <!-- FAQ -->
      <section id="faq" class="section">
        <div class="wrap faq">
          <p class="eyebrow">Questions</p>
          <h2 data-reveal class="section-title">FAQ</h2>
          <div class="faq__list" data-reveal>
            <details
              v-for="(f, i) in faqs"
              :key="f.q"
              :open="open === i"
              class="faq__item"
              @toggle="(e: Event) => { if ((e.target as HTMLDetailsElement).open) open = i }"
            >
              <summary>{{ f.q }}</summary>
              <p>{{ f.a }}</p>
            </details>
          </div>
        </div>
      </section>

      <!-- Download -->
      <section id="download" class="section section--alt download">
        <div class="wrap download__inner">
          <img
            class="download__icon"
            :src="`${base}icon.png`"
            alt=""
            width="96"
            height="96"
            loading="lazy"
          />
          <h2 data-reveal class="section-title download__title">Get Asura</h2>
          <p data-reveal style="--rd: 1" class="section-lede download__lede">
            Free early release. Signed and notarized macOS build for Apple
            silicon; Windows and Linux build from source until their
            packages ship.
          </p>
          <div class="hero__ctas download__ctas">
            <a
              class="btn btn--primary"
              href="https://github.com/terion-labs/asura/releases/latest/download/Asura-macOS-arm64.zip"
            >
              Download for macOS
              <span class="btn__sub">.zip · arm64</span>
            </a>
            <a class="btn" href="https://github.com/terion-labs/asura">
              Build from source
            </a>
          </div>
          <p class="download__checksum">
            Verify the archive against its
            <a
              href="https://github.com/terion-labs/asura/releases/latest/download/Asura-macOS-arm64.zip.sha256"
            >SHA-256 checksum</a>.
          </p>
        </div>
      </section>
    </main>

    <SiteFooter />
  </div>
</template>

<style scoped>
/* alpha notice */
.notice {
  background: var(--accent-soft);
  border-bottom: 1px solid color-mix(in srgb, var(--accent) 30%, transparent);
}

.notice__row {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 10px;
  padding-block: 9px;
  font-size: 13px;
  color: var(--muted);
  text-align: center;
}

.notice__badge {
  flex-shrink: 0;
  font-family: var(--mono);
  font-size: 10.5px;
  font-weight: 700;
  letter-spacing: 0.09em;
  text-transform: uppercase;
  color: #16120c;
  background: var(--accent);
  border-radius: 999px;
  padding: 2px 9px;
}

/* session continuity glow */
.sessions__shot { position: relative; }

.sessions__glow {
  position: absolute;
  right: 26px;
  bottom: 26px;
  width: 68px;
  height: 68px;
  display: grid;
  place-items: center;
  border-radius: 18px;
  background: rgba(14, 14, 16, 0.82);
  border: 1px solid color-mix(in srgb, var(--accent) 45%, transparent);
  box-shadow:
    0 0 26px rgba(195, 117, 41, 0.55),
    0 0 70px rgba(195, 117, 41, 0.28),
    inset 0 0 14px rgba(195, 117, 41, 0.18);
}

.sessions__glow svg {
  width: 36px;
  height: 36px;
  fill: var(--accent);
  filter: drop-shadow(0 0 8px rgba(195, 117, 41, 0.9));
}

/* hero */
.hero { padding-top: 88px; }

@keyframes rise {
  from {
    opacity: 0;
    transform: translateY(18px);
  }
  to {
    opacity: 1;
    transform: none;
  }
}

.hero__inner > * {
  animation: rise 0.7s cubic-bezier(0.22, 0.61, 0.36, 1) backwards;
}

.hero__inner > *:nth-child(2) { animation-delay: 0.08s; }
.hero__inner > *:nth-child(3) { animation-delay: 0.16s; }
.hero__inner > *:nth-child(4) { animation-delay: 0.24s; }
.hero__inner > *:nth-child(5) { animation-delay: 0.32s; }

.hero__shot {
  animation: rise 0.85s cubic-bezier(0.22, 0.61, 0.36, 1) 0.3s backwards;
}

.hero__inner {
  max-width: 780px;
  margin-inline: auto;
  text-align: center;
  display: flex;
  flex-direction: column;
  align-items: center;
}

.hero__title {
  font-size: clamp(40px, 6.4vw, 72px);
  letter-spacing: -0.03em;
}

.hero__lede {
  margin-top: 22px;
  font-size: clamp(16px, 2vw, 18.5px);
  color: var(--muted);
  max-width: 58ch;
}

.hero__ctas {
  display: flex;
  gap: 12px;
  margin-top: 34px;
  flex-wrap: wrap;
  justify-content: center;
}

.hero__badges {
  display: flex;
  flex-wrap: wrap;
  gap: 10px 26px;
  justify-content: center;
  list-style: none;
  padding: 0;
  margin: 36px 0 0;
  color: var(--faint);
  font-size: 13px;
}

.hero__shot {
  margin-top: 64px;
}

/* quad */
.quad {
  grid-template-columns: repeat(2, 1fr);
  margin-top: 44px;
}

@media (max-width: 720px) {
  .quad { grid-template-columns: 1fr; }
}

/* trio */
.trio {
  grid-template-columns: repeat(3, 1fr);
  margin-top: 44px;
}

@media (max-width: 900px) {
  .trio { grid-template-columns: 1fr; }
}

/* split sections */
.split {
  display: grid;
  grid-template-columns: minmax(0, 5fr) minmax(0, 7fr);
  gap: 56px;
  align-items: center;
}

.split--flip { grid-template-columns: minmax(0, 5fr) minmax(0, 7fr); }

@media (max-width: 980px) {
  .split, .split--flip { grid-template-columns: 1fr; gap: 36px; }
}

.checks {
  list-style: none;
  padding: 0;
  margin: 28px 0 0;
  display: flex;
  flex-direction: column;
  gap: 14px;
}

.checks li {
  position: relative;
  padding-left: 26px;
  color: var(--muted);
  font-size: 14.5px;
}

.checks li::before {
  content: '';
  position: absolute;
  left: 0;
  top: 7px;
  width: 12px;
  height: 8px;
  border-left: 2px solid var(--accent);
  border-bottom: 2px solid var(--accent);
  transform: rotate(-45deg);
}

.providers { margin-top: 34px; }

.providers__label {
  font-size: 12px;
  font-family: var(--mono);
  letter-spacing: 0.1em;
  text-transform: uppercase;
  color: var(--faint);
  margin-bottom: 12px;
}

.providers__list {
  list-style: none;
  padding: 0;
  margin: 0;
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.providers__list li {
  font-size: 12.5px;
  color: var(--muted);
  background: var(--surface);
  border: 1px solid var(--line-soft);
  border-radius: 999px;
  padding: 4px 12px;
}

.panels-tour { margin-top: 40px; }

/* faq */
.faq__list {
  margin-top: 40px;
  border-top: 1px solid var(--line-soft);
}

.faq__item {
  border-bottom: 1px solid var(--line-soft);
}

.faq__item summary {
  cursor: pointer;
  padding: 20px 0;
  font-weight: 600;
  font-size: 16px;
  list-style: none;
  position: relative;
  padding-right: 36px;
}

.faq__item summary::-webkit-details-marker { display: none; }

.faq__item summary::after {
  content: '+';
  position: absolute;
  right: 4px;
  top: 50%;
  transform: translateY(-50%);
  color: var(--accent);
  font-size: 20px;
  font-weight: 400;
}

.faq__item[open] summary::after { content: '−'; }

.faq__item p {
  color: var(--muted);
  font-size: 15px;
  padding-bottom: 22px;
  max-width: 76ch;
}

/* download */
.download__inner {
  text-align: center;
  display: flex;
  flex-direction: column;
  align-items: center;
}

.download__icon {
  width: 96px;
  height: 96px;
  border-radius: 22px;
  margin-bottom: 26px;
  filter: drop-shadow(0 18px 40px rgba(195, 117, 41, 0.28));
}

.download__title { max-width: none; }
.download__lede { text-align: center; }
.download__ctas { margin-top: 30px; }

.download__checksum {
  margin-top: 18px;
  font-size: 12.5px;
  color: var(--faint);
}

.download__checksum a { color: var(--muted); }
</style>
