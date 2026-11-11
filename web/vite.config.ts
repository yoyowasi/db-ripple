import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// API 서버(DbRipple.Host)는 http://127.0.0.1:5080 에서 돈다. 브라우저는 같은 출처(/api)로만 부르므로 CORS가 필요 없다.
export default defineConfig({
  plugins: [vue()],
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: 'http://127.0.0.1:5080',
        changeOrigin: false,
        // 미리보기는 끝날 때까지 기다리는 동기 요청이다(서버 쪽 제한 시간이 있다). 개발 프록시는 끊지 않는다.
        timeout: 0,
        proxyTimeout: 0,
      },
    },
  },
  build: { target: 'es2022', sourcemap: true },
})
