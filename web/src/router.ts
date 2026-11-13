import { createRouter, createWebHistory } from 'vue-router'
import type { RouteRecordRaw } from 'vue-router'

/** /sp9 는 개발용 측정 화면이다. 개발 서버에서만 열리고, 운영 빌드에서 열려면 VITE_ENABLE_SP9=true. */
export const sp9Enabled = import.meta.env.DEV || import.meta.env.VITE_ENABLE_SP9 === 'true'

const routes: RouteRecordRaw[] = [
  { path: '/', name: 'home', component: () => import('./views/HomeView.vue') },
  { path: '/settings', name: 'settings', component: () => import('./views/SettingsView.vue') },
]
if (sp9Enabled) {
  routes.push({ path: '/sp9', name: 'sp9', component: () => import('./views/Sp9View.vue') })
}
routes.push({ path: '/:rest(.*)*', redirect: '/' })

export const router = createRouter({
  history: createWebHistory(),
  routes,
})

// 개발 서버의 시험 데이터(?fixture=…)는 화면을 옮겨도 이어진다. 운영 빌드에서는 이 분기가 빠진다.
if (import.meta.env.DEV) {
  router.beforeEach((to, from) => {
    const fx = from.query.fixture
    if (fx && !to.query.fixture) return { ...to, query: { ...to.query, fixture: fx } }
    return true
  })
}
