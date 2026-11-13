import { createApp } from 'vue'
import { createPinia } from 'pinia'
import { AllCommunityModule, ModuleRegistry } from 'ag-grid-community'
import '@vue-flow/core/dist/style.css'
import App from './App.vue'
import { router } from './router'
import { syncAgThemeMode } from './lib/theme'
import './styles/fonts.css'
import './styles/main.css'

// AG Grid 33+ 는 사용할 모듈을 직접 등록한다. Community 전체 모듈이면 충분하다.
ModuleRegistry.registerModules([AllCommunityModule])
syncAgThemeMode()

createApp(App).use(createPinia()).use(router).mount('#app')
