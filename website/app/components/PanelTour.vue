<script setup lang="ts">
const base = useRuntimeConfig().app.baseURL

// Fluent System Icons (20/regular), the same set the app's panel launcher uses.
const icons: Record<string, string> = {
  terminal:
    'M5.64645 9.14645C5.84171 8.95118 6.15829 8.95118 6.35355 9.14645L8.35355 11.1464C8.44732 11.2402 8.5 11.3674 8.5 11.5C8.5 11.6326 8.44732 11.7598 8.35355 11.8536L6.35355 13.8536C6.15829 14.0488 5.84171 14.0488 5.64645 13.8536C5.45118 13.6583 5.45118 13.3417 5.64645 13.1464L7.29289 11.5L5.64645 9.85355C5.45118 9.65829 5.45118 9.34171 5.64645 9.14645ZM14.5 13H9.5C9.22386 13 9 13.2239 9 13.5C9 13.7761 9.22386 14 9.5 14H14.5C14.7761 14 15 13.7761 15 13.5C15 13.2239 14.7761 13 14.5 13ZM2.99609 5.5C2.99609 4.11929 4.11538 3 5.49609 3H14.4961C15.8768 3 16.9961 4.11929 16.9961 5.5V6H16.999V7H16.9961V14.5C16.9961 15.8807 15.8768 17 14.4961 17H5.49609C4.11538 17 2.99609 15.8807 2.99609 14.5V5.5ZM15.9961 6V5.5C15.9961 4.67157 15.3245 4 14.4961 4H5.49609C4.66767 4 3.99609 4.67157 3.99609 5.5V6H15.9961ZM3.99609 7V14.5C3.99609 15.3284 4.66767 16 5.49609 16H14.4961C15.3245 16 15.9961 15.3284 15.9961 14.5V7H3.99609Z',
  globe:
    'M10 18C14.4183 18 18 14.4183 18 10C18 5.58172 14.4183 2 10 2C5.58172 2 2 5.58172 2 10C2 14.4183 5.58172 18 10 18ZM10 3C10.6568 3 11.4068 3.59025 12.0218 4.90814C12.2393 5.37419 12.4283 5.90978 12.5806 6.5H7.41936C7.57172 5.90978 7.76073 5.37419 7.97822 4.90814C8.59323 3.59025 9.34315 3 10 3ZM7.07203 4.48526C6.79564 5.07753 6.56498 5.75696 6.38931 6.5H3.93648C4.77295 5.05399 6.11182 3.93497 7.71442 3.38163C7.47297 3.71222 7.25828 4.08617 7.07203 4.48526ZM6.19265 7.5C6.06723 8.28832 6 9.12934 6 10C6 10.8707 6.06723 11.7117 6.19265 12.5H3.45963C3.16268 11.7236 3 10.8808 3 10C3 9.1192 3.16268 8.2764 3.45963 7.5H6.19265ZM6.38931 13.5C6.56498 14.243 6.79564 14.9225 7.07203 15.5147C7.25828 15.9138 7.47297 16.2878 7.71442 16.6184C6.11182 16.065 4.77295 14.946 3.93648 13.5H6.38931ZM7.41936 13.5H12.5806C12.4283 14.0902 12.2393 14.6258 12.0218 15.0919C11.4068 16.4097 10.6568 17 10 17C9.34315 17 8.59323 16.4097 7.97822 15.0919C7.76073 14.6258 7.57172 14.0902 7.41936 13.5ZM12.7938 12.5H7.20617C7.07345 11.7253 7 10.8833 7 10C7 9.11669 7.07345 8.27472 7.20617 7.5H12.7938C12.9266 8.27472 13 9.11669 13 10C13 10.8833 12.9266 11.7253 12.7938 12.5ZM13.6107 13.5H16.0635C15.2271 14.946 13.8882 16.065 12.2856 16.6184C12.527 16.2878 12.7417 15.9138 12.928 15.5147C13.2044 14.9225 13.435 14.243 13.6107 13.5ZM16.5404 12.5H13.8074C13.9328 11.7117 14 10.8707 14 10C14 9.12934 13.9328 8.28832 13.8074 7.5H16.5404C16.8373 8.2764 17 9.1192 17 10C17 10.8808 16.8373 11.7236 16.5404 12.5ZM12.2856 3.38163C13.8882 3.93497 15.2271 5.05399 16.0635 6.5H13.6107C13.435 5.75696 13.2044 5.07753 12.928 4.48526C12.7417 4.08617 12.527 3.71222 12.2856 3.38163Z',
  database:
    'M4 5C4 3.993 4.87513 3.24472 5.90401 2.77705C6.97802 2.28886 8.42664 2 10 2C11.5734 2 13.022 2.28886 14.096 2.77705C15.1249 3.24472 16 3.993 16 5V15C16 16.007 15.1249 16.7553 14.096 17.2229C13.022 17.7111 11.5734 18 10 18C8.42664 18 6.97802 17.7111 5.90401 17.2229C4.87513 16.7553 4 16.007 4 15V5ZM5 5C5 5.37372 5.35608 5.87543 6.31781 6.31258C7.23441 6.72922 8.53579 7 10 7C11.4642 7 12.7656 6.72922 13.6822 6.31258C14.6439 5.87543 15 5.37372 15 5C15 4.62628 14.6439 4.12457 13.6822 3.68742C12.7656 3.27078 11.4642 3 10 3C8.53579 3 7.23441 3.27078 6.31781 3.68742C5.35608 4.12457 5 4.62628 5 5ZM15 6.69813C14.729 6.90046 14.4201 7.07563 14.096 7.22295C13.022 7.71114 11.5734 8 10 8C8.42664 8 6.97802 7.71114 5.90401 7.22295C5.5799 7.07563 5.27105 6.90046 5 6.69813V15C5 15.3737 5.35608 15.8754 6.31781 16.3126C7.23441 16.7292 8.53579 17 10 17C11.4642 17 12.7656 16.7292 13.6822 16.3126C14.6439 15.8754 15 15.3737 15 15V6.69813Z',
  box: 'M11.2999 2.4808C10.4654 2.14702 9.53457 2.14702 8.70013 2.4808L2.94291 4.78369C2.37343 5.01148 2 5.56305 2 6.1764V13.8223C2 14.4357 2.37343 14.9873 2.94291 15.2151L8.70013 17.5179C9.53457 17.8517 10.4654 17.8517 11.2999 17.5179L17.0571 15.2151C17.6266 14.9873 18 14.4357 18 13.8223V6.1764C18 5.56305 17.6266 5.01148 17.0571 4.78369L11.2999 2.4808ZM9.07152 3.40928C9.66755 3.17087 10.3324 3.17087 10.9285 3.40928L16.1538 5.49941L13.8751 6.41088L7.72133 3.94935L9.07152 3.40928ZM6.37504 4.48787L12.5289 6.94939L10.0001 7.96088L3.84633 5.49935L6.37504 4.48787ZM10.5001 8.83791L17 6.23797V13.8223C17 14.0268 16.8755 14.2106 16.6857 14.2866L10.9285 16.5895C10.7889 16.6453 10.6455 16.6881 10.5001 16.7177V8.83791ZM9.50015 8.83791V16.7178C9.35467 16.6881 9.21121 16.6453 9.07152 16.5895L3.3143 14.2866C3.12448 14.2106 3 14.0268 3 13.8223V6.23785L9.50015 8.83791Z',
  branch:
    'M9 5C9 3.34315 7.65685 2 6 2C4.34315 2 3 3.34315 3 5C3 6.4865 4.08114 7.72048 5.5 7.95852V12.0415C4.08114 12.2795 3 13.5135 3 15C3 16.6569 4.34315 18 6 18C7.65685 18 9 16.6569 9 15C9 13.5135 7.91886 12.2795 6.5 12.0415V11H12C13.3807 11 14.5 9.88071 14.5 8.5V7.95852C15.9189 7.72048 17 6.4865 17 5C17 3.34315 15.6569 2 14 2C12.3431 2 11 3.34315 11 5C11 6.4865 12.0811 7.72048 13.5 7.95852V8.5C13.5 9.32843 12.8284 10 12 10H6.5V7.95852C7.91886 7.72048 9 6.4865 9 5ZM6 7C4.89543 7 4 6.10457 4 5C4 3.89543 4.89543 3 6 3C7.10457 3 8 3.89543 8 5C8 6.10457 7.10457 7 6 7ZM6 17C4.89543 17 4 16.1046 4 15C4 13.8954 4.89543 13 6 13C7.10457 13 8 13.8954 8 15C8 16.1046 7.10457 17 6 17ZM16 5C16 6.10457 15.1046 7 14 7C12.8954 7 12 6.10457 12 5C12 3.89543 12.8954 3 14 3C15.1046 3 16 3.89543 16 5Z',
  folder:
    'M4.5 3C3.11929 3 2 4.11929 2 5.5V14.5C2 15.8807 3.11929 17 4.5 17H15.5C16.8807 17 18 15.8807 18 14.5V7.5C18 6.11929 16.8807 5 15.5 5H9.70711L8.21967 3.51256C7.89148 3.18437 7.44636 3 6.98223 3H4.5ZM3 5.5C3 4.67157 3.67157 4 4.5 4H6.98223C7.18115 4 7.37191 4.07902 7.51256 4.21967L8.79289 5.5L7.43934 6.85355C7.34557 6.94732 7.21839 7 7.08579 7H3V5.5ZM3 8H7.08579C7.48361 8 7.86514 7.84196 8.14645 7.56066L9.70711 6H15.5C16.3284 6 17 6.67157 17 7.5V14.5C17 15.3284 16.3284 16 15.5 16H4.5C3.67157 16 3 15.3284 3 14.5V8Z',
  gauge:
    'M12.4659 5.05664C12.3656 5.31394 12.0758 5.44125 11.8185 5.34099C10.0251 4.64221 7.91131 5.0177 6.46447 6.46454C4.51185 8.41716 4.51185 11.583 6.46447 13.5356C6.65974 13.7309 6.65974 14.0475 6.46447 14.2427C6.26921 14.438 5.95263 14.438 5.75737 14.2427C3.41422 11.8996 3.41422 8.10058 5.75737 5.75743C7.49478 4.02002 10.0318 3.5716 12.1815 4.40923C12.4388 4.50948 12.5661 4.79934 12.4659 5.05664ZM14.9435 7.53421C15.2008 7.43396 15.4906 7.56127 15.5909 7.81857C16.4285 9.96831 15.9801 12.5053 14.2426 14.2427C14.0474 14.438 13.7308 14.438 13.5355 14.2427C13.3403 14.0475 13.3403 13.7309 13.5355 13.5356C14.9824 12.0888 15.3579 9.97504 14.6591 8.18162C14.5588 7.92432 14.6862 7.63447 14.9435 7.53421ZM14.0852 5.81922C13.9034 5.66396 13.6371 5.65914 13.4498 5.80771L13.2734 5.94787C13.1611 6.03724 13.0002 6.16534 12.8061 6.32036C12.418 6.63035 11.8964 7.04822 11.3635 7.4794C10.831 7.91028 10.2854 8.35575 9.84968 8.72053C9.63205 8.90274 9.4397 9.06659 9.28926 9.19929C9.14937 9.3227 9.01831 9.44276 8.94631 9.52707C8.40834 10.1571 8.48293 11.1039 9.11291 11.6418C9.74289 12.1798 10.6897 12.1052 11.2277 11.4752C11.2997 11.3909 11.3977 11.2427 11.4977 11.0852C11.6052 10.9158 11.7369 10.7002 11.8828 10.4567C12.1749 9.9693 12.5294 9.3607 12.8716 8.76726C13.214 8.17339 13.5451 7.5929 13.7905 7.16099C13.9132 6.945 14.0146 6.76609 14.0852 6.64114L14.1961 6.44499C14.3135 6.23672 14.267 5.97448 14.0852 5.81922ZM10 18C14.4183 18 18 14.4183 18 10C18 5.58172 14.4183 2 10 2C5.58172 2 2 5.58172 2 10C2 14.4183 5.58172 18 10 18ZM10 17C6.13401 17 3 13.866 3 10C3 6.13401 6.13401 3 10 3C13.866 3 17 6.13401 17 10C17 13.866 13.866 17 10 17Z',
  pulse:
    'M14 3C15.6569 3 17 4.34315 17 6V14C17 15.6569 15.6569 17 14 17H6C4.34315 17 3 15.6569 3 14V6C3 4.34315 4.34315 3 6 3H14ZM6 4C4.89543 4 4 4.89543 4 6V14C4 15.1046 4.89543 16 6 16H14C15.1046 16 16 15.1046 16 14V6C16 4.89543 15.1046 4 14 4H6ZM8.50391 6C8.71376 6.00165 8.90012 6.13439 8.9707 6.33203L11.0771 12.2285L12.0527 10.2764C12.1374 10.107 12.3106 10 12.5 10H14.5C14.7761 10 15 10.2239 15 10.5C15 10.7761 14.7761 11 14.5 11H12.8086L11.4473 13.7236C11.3577 13.9028 11.1697 14.0111 10.9697 13.999C10.7699 13.9869 10.5966 13.8566 10.5293 13.668L8.48828 7.9541L7.46777 10.6758C7.39451 10.8708 7.20831 11 7 11H5.5C5.22386 11 5 10.7761 5 10.5C5 10.2239 5.22386 10 5.5 10H6.65332L8.03223 6.32422C8.106 6.12791 8.29418 5.99836 8.50391 6Z',
}

const panels = [
  {
    id: 'terminal',
    label: 'Terminal',
    icon: 'terminal',
    shot: 'workspace.webp',
    alt: 'Asura window with an SSH terminal running systemctl next to a browser panel',
    text: 'The Ghostty engine, rendered natively. Inline images, clickable links, scrollback that knows where each command starts and ends. A local shell and a remote SSH session are the same panel with a different host.',
  },
  {
    id: 'browser',
    label: 'Browser',
    icon: 'globe',
    shot: 'workspace-browser.webp',
    alt: 'Embedded Chromium browser panel inside an Asura workspace',
    text: 'Embedded Chromium as an ordinary panel. Bind it to an SSH connection and it routes through that tunnel, so remote localhost ports and private subnet addresses load like local pages.',
  },
  {
    id: 'database',
    label: 'Databases',
    icon: 'database',
    shot: 'workspace-database.webp',
    alt: 'Database panel showing a table grid and SQL editor',
    text: 'Postgres, MySQL, MariaDB, SQLite, SQL Server, ClickHouse, DuckDB, Oracle, Firebird, CockroachDB, Redshift. Browse tables, edit rows inline, write SQL with an editor that understands your schema. Remote databases connect through the same SSH tunnels.',
  },
  {
    id: 'redis',
    label: 'Redis',
    icon: 'database',
    shot: 'workspace-redis.webp',
    alt: 'Redis panel with a key tree and JSON value view',
    text: 'A key browser with type-aware views, search, TTLs, and pub/sub. JSON values render as trees, not strings.',
  },
  {
    id: 'docker',
    label: 'Docker',
    icon: 'box',
    shot: 'workspace-docker-logs.webp',
    alt: 'Docker panel streaming and searching container logs',
    text: 'Containers, images, volumes, and networks for a local daemon or a remote engine. Stream logs, search them, exec a shell, watch per-container stats.',
  },
  {
    id: 'git',
    label: 'Git',
    icon: 'branch',
    shot: 'workspace-git.webp',
    alt: 'Git panel with staged files and a diff view',
    text: 'Stage hunks, write commits, walk history with a commit graph. It works against the repository, not a bundled copy of it.',
  },
  {
    id: 'files',
    label: 'Files',
    icon: 'folder',
    shot: 'workspace-file-viewer.webp',
    alt: 'File viewer panel listing a remote directory',
    text: 'One file panel for local disks, SFTP, FTP, S3, WebDAV, and SMB. Transfers run with progress and survive panel moves.',
  },
  {
    id: 'monitor',
    label: 'Processes',
    icon: 'gauge',
    shot: 'workspace-process-monitor.webp',
    alt: 'Process monitor panel with CPU and memory per process',
    text: 'Live process lists with CPU, memory, and PID, plus search and kill. Point it at a remote host and it watches over plain SSH. No agent on the box.',
  },
  {
    id: 'stats',
    label: 'Statistics',
    icon: 'pulse',
    shot: 'workspace-statistics.webp',
    alt: 'Statistics panel charting CPU, memory, disk, and network',
    text: 'CPU, memory, disk, and network charts for the machine you pick, local or remote. Nothing gets installed on the server to make it happen.',
  },
]

const active = ref(panels[0]!)
</script>

<template>
  <div class="tour">
    <div class="tour__tabs" role="tablist" aria-label="Panels">
      <button
        v-for="p in panels"
        :key="p.id"
        role="tab"
        :aria-selected="active.id === p.id"
        :class="['tour__tab', { 'tour__tab--on': active.id === p.id }]"
        @click="active = p"
      >
        <svg viewBox="0 0 20 20" aria-hidden="true">
          <path :d="icons[p.icon]" />
        </svg>
        {{ p.label }}
      </button>
    </div>
    <Transition name="swap" mode="out-in">
      <p :key="active.id" class="tour__text">{{ active.text }}</p>
    </Transition>
    <div class="shot tour__shot">
      <Transition name="shotswap" mode="out-in">
        <img
          :key="active.id"
          :src="`${base}shots/${active.shot}`"
          :alt="active.alt"
          width="2880"
          height="1800"
          loading="lazy"
        />
      </Transition>
    </div>
  </div>
</template>

<style scoped>
.tour__tabs {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin-bottom: 22px;
}

.tour__tab {
  display: inline-flex;
  align-items: center;
  gap: 7px;
  font: inherit;
  font-size: 13.5px;
  font-weight: 500;
  color: var(--muted);
  background: var(--surface);
  border: 1px solid var(--line-soft);
  border-radius: 999px;
  padding: 7px 15px 7px 12px;
  cursor: pointer;
  transition: color 0.15s ease, border-color 0.15s ease, background 0.15s ease;
}

.tour__tab svg {
  width: 15px;
  height: 15px;
  fill: currentColor;
  opacity: 0.85;
}

.tour__tab:hover { color: var(--text); border-color: var(--line); }

.tour__tab--on {
  color: var(--accent);
  background: var(--accent-soft);
  border-color: color-mix(in srgb, var(--accent) 45%, transparent);
}

.tour__text {
  color: var(--muted);
  font-size: 15.5px;
  max-width: 74ch;
  min-height: 3.2em;
  margin-bottom: 24px;
}

.tour__shot { aspect-ratio: 8 / 5; }

.tour__shot img {
  width: 100%;
  height: 100%;
  object-fit: cover;
}

/* tab switch transitions */
.swap-enter-active {
  transition: opacity 0.28s ease, transform 0.28s ease;
}

.swap-leave-active {
  transition: opacity 0.12s ease;
}

.swap-enter-from {
  opacity: 0;
  transform: translateY(6px);
}

.swap-leave-to { opacity: 0; }

.shotswap-enter-active {
  transition: opacity 0.35s ease, transform 0.35s ease;
}

.shotswap-leave-active {
  transition: opacity 0.15s ease;
}

.shotswap-enter-from {
  opacity: 0;
  transform: scale(1.012);
}

.shotswap-leave-to { opacity: 0; }
</style>
