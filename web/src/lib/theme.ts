// AG Grid 테마 모드를 OS 설정(prefers-color-scheme)에 맞춘다. 앱 색은 CSS 변수가 따로 따른다.
import { colorSchemeVariable, themeQuartz } from 'ag-grid-community'

export const gridTheme = themeQuartz.withPart(colorSchemeVariable).withParams({
  fontSize: 13,
  headerFontSize: 12,
  spacing: 5,
  rowHeight: 30,
  headerHeight: 32,
})

export function syncAgThemeMode(): void {
  if (typeof window === 'undefined' || !window.matchMedia) return
  const mq = window.matchMedia('(prefers-color-scheme: dark)')
  const apply = () => {
    document.body.dataset.agThemeMode = mq.matches ? 'dark' : 'light'
  }
  apply()
  mq.addEventListener('change', apply)
}
