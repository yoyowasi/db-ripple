// 첫 그리기 전에 테마를 붙인다(src/lib/theme.ts와 같은 규칙: 저장된 선택 > OS 설정).
// 인라인 스크립트는 CSP script-src 'self'에서 막히므로 외부 파일로 둔다.
;(function () {
  var t = null
  try {
    t = localStorage.getItem('dbr-theme')
  } catch (e) {}
  if (t !== 'dark' && t !== 'light') t = window.matchMedia && matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'
  document.documentElement.setAttribute('data-theme', t)
})()
