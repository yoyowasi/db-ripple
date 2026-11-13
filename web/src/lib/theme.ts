// 앱 테마. <html data-theme="light|dark">를 정하고 AG Grid(body data-ag-theme-mode)·주소창 색이 따라간다.
// CodeMirror는 CSS 변수로 칠하므로 따로 할 일이 없다. 첫 값: 저장된 선택 > OS prefers-color-scheme.
// index.html 의 인라인 스크립트가 같은 규칙으로 첫 화면 전에 data-theme를 먼저 붙인다(깜빡임 방지).
import { colorSchemeVariable, themeQuartz } from 'ag-grid-community'
import { readonly, shallowRef } from 'vue'

export type Theme = 'light' | 'dark'

const STORAGE_KEY = 'dbr-theme'
const THEME_COLOR: Record<Theme, string> = { light: '#e6ebee', dark: '#091116' }

export const gridTheme = themeQuartz.withPart(colorSchemeVariable).withParams({
  fontSize: 13,
  headerFontSize: 12,
  spacing: 5,
  rowHeight: 30,
  headerHeight: 32,
  borderRadius: 4,
  wrapperBorderRadius: 4,
})

const theme = shallowRef<Theme>('light')
let stored: Theme | null = null
let started = false

function readStored(): Theme | null {
  try {
    const v = localStorage.getItem(STORAGE_KEY)
    return v === 'dark' || v === 'light' ? v : null
  } catch {
    return null
  }
}

function osTheme(): Theme {
  return window.matchMedia?.('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'
}

function apply(t: Theme): void {
  theme.value = t
  document.documentElement.dataset.theme = t
  document.body.dataset.agThemeMode = t
  document.querySelector('meta[name="theme-color"]')?.setAttribute('content', THEME_COLOR[t])
}

/** 첫 테마를 붙이고, 사용자가 고르지 않았으면 OS 설정 변화를 따른다. main.ts가 앱 마운트 전에 한 번 부른다. */
export function syncAgThemeMode(): void {
  if (typeof window === 'undefined' || started) return
  started = true
  stored = readStored()
  apply(stored ?? osTheme())
  window.matchMedia?.('(prefers-color-scheme: dark)').addEventListener('change', () => {
    if (!stored) apply(osTheme())
  })
}

/** 사용자가 고른 테마. 저장해 다음 방문에도 쓴다(저장 실패는 무시). */
export function setTheme(t: Theme): void {
  stored = t
  apply(t)
  try {
    localStorage.setItem(STORAGE_KEY, t)
  } catch {
    // 개인 창 등에서 저장이 막혀도 이번 화면에는 적용된다.
  }
}

export function useTheme() {
  return {
    theme: readonly(theme),
    setTheme,
    toggle: () => setTheme(theme.value === 'dark' ? 'light' : 'dark'),
  }
}
