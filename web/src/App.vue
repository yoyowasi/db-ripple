<script setup lang="ts">
import { defineAsyncComponent, onBeforeUnmount, onMounted } from 'vue'
import { RouterLink, RouterView, useRoute, useRouter } from 'vue-router'
import { getSettings } from './api/client'
import { sp9Enabled } from './router'
import { useSandboxStore } from './stores/sandbox'

// 개발 서버에서만 쓰는 시험 데이터 선택. 운영 빌드에서는 import.meta.env.DEV가 false라 이 청크가 빠진다.
const FixturePicker = import.meta.env.DEV ? defineAsyncComponent(() => import('./dev/FixturePicker.vue')) : null

const sandbox = useSandboxStore()
const route = useRoute()
const router = useRouter()

// 첫 실행(설정 없음)이면 홈에서 설정 화면으로 안내한다. 설정 API가 없는 옛 서버·읽기 실패는 조용히 넘어간다.
async function guideFirstRun(): Promise<void> {
  try {
    await router.isReady()
    const s = await getSettings()
    if (!s.configured && router.currentRoute.value.name === 'home') await router.replace({ name: 'settings', query: route.query })
  } catch {
    /* 설정 화면은 [설정]에서 직접 연다 */
  }
}

onMounted(() => {
  sandbox.start()
  void guideFirstRun()
})
onBeforeUnmount(() => sandbox.stop())
</script>

<template>
  <a class="skip-link" href="#main">본문으로 건너뛰기</a>
  <h1 class="sr-only">db-ripple 영향 미리보기 워크벤치</h1>
  <nav class="menu" aria-label="메뉴 막대">
    <RouterLink to="/" class="logo">db-ripple</RouterLink>
    <details class="help">
      <summary>도움말</summary>
      <div class="help-body">
        <p>SQL(INSERT·UPDATE·DELETE)을 샌드박스에서 실행해 어떤 테이블의 몇 행이 바뀌는지 미리 보고, 같으면 원본에 적용합니다.</p>
        <ul>
          <li><kbd>Ctrl+Enter</kbd> 미리보기 실행</li>
          <li>구분선은 끌거나, 초점을 둔 뒤 화살표·Home·End 키로 크기를 바꿉니다.</li>
          <li>탭은 좌우 화살표로 옮깁니다.</li>
        </ul>
      </div>
    </details>
    <RouterLink to="/settings" class="set" active-class="on">설정</RouterLink>
    <RouterLink v-if="sp9Enabled" to="/sp9">SP9 측정(개발용)</RouterLink>
    <component :is="FixturePicker" v-if="FixturePicker" class="menu-end" />
  </nav>
  <RouterView v-slot="{ Component, route }">
    <component :is="Component" v-if="route.name === 'home'" />
    <main v-else id="main" class="page-main" tabindex="-1">
      <component :is="Component" />
    </main>
  </RouterView>
</template>

<style scoped>
.menu {
  position: relative;
  z-index: 5;
  display: flex;
  align-items: center;
  gap: 18px;
  height: var(--menu-h);
  padding: 0 var(--gx);
  background: var(--menu-bg);
  color: var(--menu-fg);
  font-size: 12px;
}
.menu :focus-visible {
  outline-color: var(--menu-focus);
}
.menu a {
  color: inherit;
  text-decoration: none;
}
.menu a:hover,
.help summary:hover {
  color: var(--menu-fg-strong);
  text-decoration: underline;
}
.menu .set.on {
  color: var(--menu-fg-strong);
  text-decoration: underline;
}
.menu .logo {
  margin-right: 6px;
  color: var(--menu-fg-strong);
  font: 700 13px var(--mono);
}
.help {
  position: relative;
}
.help summary {
  cursor: pointer;
  list-style: none;
  padding: 4px 0;
}
.help summary::-webkit-details-marker {
  display: none;
}
.help-body {
  position: absolute;
  top: calc(100% + 4px);
  left: 0;
  width: min(360px, calc(100vw - 32px));
  padding: 10px 12px;
  background: var(--panel);
  color: var(--fg);
  border: 1px solid var(--line);
  border-radius: var(--r);
  box-shadow: 0 6px 18px rgb(0 0 0 / 0.18);
  font-size: 13px;
}
.help-body ul {
  margin: 6px 0 0;
  padding-left: 18px;
}
kbd {
  font: 12px var(--mono);
}
.menu-end {
  margin-left: auto;
}
.page-main {
  padding: var(--gx);
  outline: none;
}
@media (max-width: 999px) {
  .menu {
    gap: 10px;
  }
}
</style>
