// asura.sh serves the site at the domain root. Override for preview subpaths.
const baseURL = process.env.NUXT_APP_BASE_URL ?? '/'

export default defineNuxtConfig({
  compatibilityDate: '2025-07-15',
  ssr: true,
  // Something else on this machine polls localhost:3000 (a VS Code
  // extension's /api/board); its dropped connections crash-loop the dev
  // server. Any quiet port works.
  devServer: { port: 4180 },
  devtools: { enabled: false },
  css: ['~/assets/css/main.css'],
  app: {
    baseURL,
    buildAssetsDir: 'assets',
    head: {
      htmlAttrs: { lang: 'en' },
      link: [
        { rel: 'icon', type: 'image/svg+xml', href: `${baseURL}favicon.svg` },
        { rel: 'apple-touch-icon', href: `${baseURL}icon.png` },
        { rel: 'preconnect', href: 'https://fonts.googleapis.com' },
        {
          rel: 'preconnect',
          href: 'https://fonts.gstatic.com',
          crossorigin: '',
        },
        {
          rel: 'stylesheet',
          href: 'https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700&family=JetBrains+Mono:wght@400;500;700&display=swap',
        },
      ],
      meta: [
        { name: 'theme-color', content: '#111111' },
        { name: 'viewport', content: 'width=device-width, initial-scale=1' },
      ],
    },
  },
  nitro: {
    preset: 'static',
    prerender: { crawlLinks: true, routes: ['/', '/404.html'] },
  },
  experimental: { payloadExtraction: false },
})
