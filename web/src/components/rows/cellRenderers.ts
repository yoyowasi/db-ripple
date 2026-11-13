// AG Grid 셀 렌더러(DOM을 직접 만든다. 값은 textContent로만 넣어 이스케이프가 필요 없다).
import type { ICellRendererParams } from 'ag-grid-community'
import { VOLATILE_LABEL } from '../../lib/format'
import type { GridRow } from '../../lib/gridRows'
import { cellParts, opText } from './cellModel'

function el<K extends keyof HTMLElementTagNameMap>(tag: K, cls?: string, text?: string): HTMLElementTagNameMap[K] {
  const e = document.createElement(tag)
  if (cls) e.className = cls
  if (text !== undefined) e.textContent = text
  return e
}

/** 화면 읽기 프로그램에만 들리는 앞말("삭제됨: " 등) + 값 */
function marked(tag: 'del' | 'ins', sr: string, text: string, isNull: boolean): HTMLElement {
  const e = el(tag, isNull ? 'v-null' : undefined)
  e.append(el('span', 'sr-only', sr), text)
  return e
}

export function cellRenderer(col: string) {
  return (p: ICellRendererParams<GridRow>): HTMLElement => {
    const r = p.data
    const box = el('span', 'cell')
    if (!r) return box
    const part = cellParts(r, col)
    if (part.kind === 'none') {
      const dot = el('span', 'v-none', '·')
      dot.setAttribute('role', 'img')
      dot.setAttribute('aria-label', '해당 없음')
      box.append(dot)
      return box
    }
    if (part.kind === 'change') {
      box.append(marked('del', '삭제됨: ', part.before, part.beforeNull), el('span', 'arrow', ' → '), marked('ins', '추가됨: ', part.after, part.afterNull))
      return box
    }
    if (r.op === 'DELETE' || r.op === 'MULTISET_REMOVED') box.append(marked('del', '삭제됨: ', part.text, part.isNull))
    else if (r.op === 'INSERT' || r.op === 'MULTISET_ADDED') box.append(marked('ins', '추가됨: ', part.text, part.isNull))
    else box.append(el('span', part.isNull ? 'v-null' : undefined, part.text))
    // 새로 넣는 행의 예상값은 값 자리에서 바로 알린다(UPDATE·DELETE는 열 머리 뱃지로 충분하다).
    const why = r.volatile.get(col)
    if (why && (r.op === 'INSERT' || r.op === 'MULTISET_ADDED')) {
      const chip = el('span', 'chip chip-vol', '예상값')
      chip.title = `${VOLATILE_LABEL[why]}: 적용 때 값이 달라질 수 있습니다.`
      box.append(' ', chip)
    }
    return box
  }
}

/** 연산 열: 글자(추가·수정·삭제)가 주고, 색은 보조다. 짝 미확정은 작은 뱃지를 더한다. */
export function opRenderer(p: ICellRendererParams<GridRow>): HTMLElement {
  const r = p.data
  const box = el('span', 'cell')
  if (!r) return box
  box.append(el('span', 'op-text', opText(r)))
  if (r.unpaired) {
    const chip = el('span', 'chip chip-warn', '짝 미확정')
    chip.title = r.note
    box.append(' ', chip)
  }
  return box
}
