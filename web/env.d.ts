/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** 'true'면 운영 빌드에서도 /sp9 측정 화면을 연다(기본은 개발 서버에서만). */
  readonly VITE_ENABLE_SP9?: string
}

declare module '*.vue' {
  import type { DefineComponent } from 'vue'
  const component: DefineComponent<Record<string, unknown>, Record<string, unknown>, unknown>
  export default component
}
