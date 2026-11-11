<script setup lang="ts">
import { onBeforeUnmount, onMounted } from 'vue'
import { RouterLink, RouterView } from 'vue-router'
import SandboxBar from './components/SandboxBar.vue'
import { sp9Enabled } from './router'
import { useSandboxStore } from './stores/sandbox'

const sandbox = useSandboxStore()
onMounted(() => sandbox.start())
onBeforeUnmount(() => sandbox.stop())
</script>

<template>
  <a class="skip-link" href="#main">본문으로 건너뛰기</a>
  <header class="app-header">
    <div class="brand">
      <strong>db-ripple</strong>
      <span class="muted">DML 변경 미리보기</span>
    </div>
    <nav aria-label="화면">
      <RouterLink to="/">미리보기</RouterLink>
      <RouterLink v-if="sp9Enabled" to="/sp9">SP9 측정(개발용)</RouterLink>
    </nav>
    <SandboxBar />
  </header>
  <main id="main" class="app-main" tabindex="-1">
    <RouterView />
  </main>
</template>

<style scoped>
.app-header {
  display: flex;
  align-items: center;
  gap: 16px;
  flex-wrap: wrap;
  padding: 8px var(--gutter);
  background: var(--bg-elev);
  border-bottom: 1px solid var(--border);
}
.brand {
  display: flex;
  align-items: baseline;
  gap: 8px;
}
nav {
  display: flex;
  gap: 4px;
}
nav a {
  padding: 4px 10px;
  border-radius: var(--radius);
  text-decoration: none;
  color: var(--fg);
}
nav a.router-link-exact-active {
  background: var(--accent-soft);
  color: var(--accent);
  font-weight: 600;
}
.app-header :deep(.sandbox-bar) {
  margin-left: auto;
}
.app-main {
  padding: var(--gutter);
  outline: none;
}
</style>
