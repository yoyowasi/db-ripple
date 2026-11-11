import { createRouter, createWebHistory } from 'vue-router'
import type { RouteRecordRaw } from 'vue-router'

/** /sp9 는 개발용 측정 화면이다. 개발 서버에서만 열리고, 운영 빌드에서 열려면 VITE_ENABLE_SP9=true. */
export const sp9Enabled = import.meta.env.DEV || import.meta.env.VITE_ENABLE_SP9 === 'true'

const routes: RouteRecordRaw[] = [{ path: '/', name: 'home', component: () => import('./views/HomeView.vue') }]
if (sp9Enabled) {
  routes.push({ path: '/sp9', name: 'sp9', component: () => import('./views/Sp9View.vue') })
}
routes.push({ path: '/:rest(.*)*', redirect: '/' })

export const router = createRouter({
  history: createWebHistory(),
  routes,
})
