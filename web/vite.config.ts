import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// API 서버(DbRipple.Host)는 http://127.0.0.1:5080 에서 돈다. 브라우저는 같은 출처(/api)로만 부르므로 CORS가 필요 없다.
export default defineConfig({
  plugins: [vue()],
  server: {
    port: 5273,
    // 포트가 바뀌면 출처가 서버 허용 목록(DbRipple:AllowedOrigins)에서 벗어나 모든 POST가 403이 된다. 쓰고 있으면 옮기지 말고 실패한다.
    strictPort: true,
    // 화면과 /api가 같은 출처라 CORS가 필요 없다. Vite 기본값은 다른 localhost 출처에 CORS를 열어 프록시한 GET 응답(미리보기 결과)을 읽게 한다.
    cors: false,
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
  build: {
    target: 'es2022',
    // 실행 파일 배포본에는 소스맵을 넣지 않는다(개발 서버는 빌드와 무관하게 자체 맵을 쓴다).
    sourcemap: false,
    rollupOptions: {
      output: {
        // 큰 라이브러리를 따로 나눠 앱 청크를 작게 하고 캐시를 살린다.
        manualChunks(id: string) {
          if (!id.includes('node_modules')) return undefined
          if (id.includes('ag-grid')) return 'ag-grid'
          if (id.includes('@codemirror') || id.includes('@lezer') || id.includes('/codemirror/')) return 'codemirror'
          if (id.includes('@vue-flow') || id.includes('@dagrejs')) return 'vue-flow'
          return undefined
        },
      },
    },
  },
})
