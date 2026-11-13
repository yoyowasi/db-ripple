// 행 변경 표 전용 AG Grid 테마. 색은 앱 토큰(CSS 변수)을 따라 밝은·어두운 테마가 함께 바뀐다.
import { gridTheme } from '../../lib/theme'

export const rowsGridTheme = gridTheme.withParams({
  backgroundColor: 'var(--panel)',
  foregroundColor: 'var(--fg)',
  borderColor: 'var(--line)',
  headerBackgroundColor: 'var(--sunk)',
  headerTextColor: 'var(--fg)',
  rowHoverColor: 'color-mix(in srgb, var(--accent) 7%, transparent)',
  fontFamily: 'var(--ui)',
  cellFontFamily: 'var(--mono)',
  fontSize: 13,
  headerFontSize: 12.5,
  rowHeight: 28,
  headerHeight: 30,
  spacing: 5,
})
