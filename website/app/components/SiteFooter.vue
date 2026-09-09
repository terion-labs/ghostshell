<script setup lang="ts">
const year = new Date().getFullYear()
</script>

<template>
  <footer class="footer">
    <div class="wrap footer__row">
      <div class="footer__brand">
        <AsuraLogo class="footer__logo" />
        <div>
          <div class="footer__name"><b>Asura</b></div>
          <p class="footer__tag">Your terminal workspace.</p>
        </div>
      </div>
      <nav class="footer__links" aria-label="Footer">
        <a href="https://github.com/terion-labs/asura">GitHub</a>
        <a href="https://github.com/terion-labs/asura/releases/latest">Releases</a>
        <a href="https://github.com/terion-labs/asura/issues">Issues</a>
      </nav>
    </div>
    <div class="wrap footer__meta">
      <span>© {{ year }} terion-labs</span>
      <span>Built with .NET, Avalonia, and libghostty-vt.</span>
    </div>
  </footer>
</template>

<style scoped>
.footer {
  border-top: 1px solid var(--line-soft);
  padding: 56px 0 40px;
  background: var(--bg-alt);
}

.footer__row {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  gap: 32px;
  flex-wrap: wrap;
}

.footer__brand { display: flex; gap: 12px; align-items: flex-start; }
.footer__logo { width: 30px; height: 24px; color: var(--accent); margin-top: 3px; }
.footer__name { font-size: 16px; }
.footer__name b { font-weight: 700; }
.footer__tag { color: var(--faint); font-size: 13.5px; margin-top: 2px; }

.footer__links { display: flex; gap: 24px; font-size: 13.5px; }
.footer__links a { color: var(--muted); text-decoration: none; }
.footer__links a:hover { color: var(--text); }

.footer__meta {
  display: flex;
  justify-content: space-between;
  gap: 16px;
  flex-wrap: wrap;
  margin-top: 40px;
  color: var(--faint);
  font-size: 12.5px;
}
</style>
