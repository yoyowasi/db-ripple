// 분할 막대(role=separator). 포인터 끌기와 화살표·Home·End 키로 이웃 패널 크기를 바꾸고, 크기를 localStorage에 둔다.
import { computed, onBeforeUnmount, readonly, shallowRef } from 'vue'

export interface SplitterOptions {
  /** 저장 키 뒷부분(localStorage "dbr-split-<key>") */
  key: string
  /** x = 폭(좌우 끌기, 세로 막대), y = 높이(위아래 끌기, 가로 막대) */
  axis: 'x' | 'y'
  min: number
  max: number
  initial: number
  /** 키 한 번에 바뀌는 픽셀 */
  step?: number
}

const PREFIX = 'dbr-split-'

function load(key: string): number | null {
  try {
    const v = Number(localStorage.getItem(PREFIX + key))
    return Number.isFinite(v) && v > 0 ? v : null
  } catch {
    return null
  }
}

function save(key: string, v: number): void {
  try {
    localStorage.setItem(PREFIX + key, String(Math.round(v)))
  } catch {
    // 저장이 막혀도 이번 화면의 크기는 그대로 쓴다.
  }
}

export function useSplitter(o: SplitterOptions) {
  const step = o.step ?? 16
  const clamp = (v: number) => Math.min(o.max, Math.max(o.min, v))
  const size = shallowRef(clamp(load(o.key) ?? o.initial))
  const dragging = shallowRef(false)

  let start = 0
  let base = 0

  function set(v: number, persist: boolean): void {
    size.value = clamp(v)
    if (persist) save(o.key, size.value)
  }

  function pos(e: PointerEvent): number {
    return o.axis === 'x' ? e.clientX : e.clientY
  }

  function endDrag(): void {
    if (!dragging.value) return
    dragging.value = false
    document.body.classList.remove('dragging')
    save(o.key, size.value)
  }

  function onPointerdown(e: PointerEvent): void {
    if (e.button !== 0) return
    dragging.value = true
    start = pos(e)
    base = size.value
    ;(e.currentTarget as HTMLElement).setPointerCapture(e.pointerId)
    document.body.classList.add('dragging')
    e.preventDefault()
  }

  function onPointermove(e: PointerEvent): void {
    if (dragging.value) set(base + pos(e) - start, false)
  }

  function onKeydown(e: KeyboardEvent): void {
    const [less, more] = o.axis === 'x' ? ['ArrowLeft', 'ArrowRight'] : ['ArrowUp', 'ArrowDown']
    let next: number | null = null
    if (e.key === less) next = size.value - step
    else if (e.key === more) next = size.value + step
    else if (e.key === 'Home') next = o.min
    else if (e.key === 'End') next = o.max
    if (next === null) return
    e.preventDefault()
    set(next, true)
  }

  onBeforeUnmount(endDrag)

  /** 막대 요소에 v-bind 한다. aria-label은 쓰는 쪽이 붙인다. */
  const separatorAttrs = computed(() => ({
    role: 'separator',
    tabindex: 0,
    'aria-orientation': (o.axis === 'x' ? 'vertical' : 'horizontal') as 'vertical' | 'horizontal',
    'aria-valuemin': o.min,
    'aria-valuemax': o.max,
    'aria-valuenow': Math.round(size.value),
    onPointerdown,
    onPointermove,
    onPointerup: endDrag,
    onPointercancel: endDrag,
    onKeydown,
  }))

  return { size: readonly(size), dragging: readonly(dragging), separatorAttrs }
}
