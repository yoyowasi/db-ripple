// 한 셀에 무엇을 보일지(값 없음 · 값 · 이전 → 이후)를 정한다. 그리기는 cellRenderers.ts가 한다.
import type { GridOp, GridRow } from '../../lib/gridRows'

export const NULL_TEXT = 'NULL'

export type CellPart =
  | { kind: 'none' }
  | { kind: 'value'; text: string; isNull: boolean }
  | { kind: 'change'; before: string; after: string; beforeNull: boolean; afterNull: boolean }

const fmt = (v: string | null | undefined): string => (v === null ? NULL_TEXT : (v ?? ''))

/** DELETE·제거는 이전 값, 나머지는 이후 값을 보인다. 이미지에 열이 없으면(키만 수집) 행 키 값으로 채운다. */
export function cellParts(r: GridRow, col: string): CellPart {
  if (r.op === 'UPDATE' && r.changed.has(col)) {
    const b = r.before?.[col]
    const a = r.after?.[col]
    return { kind: 'change', before: fmt(b), after: fmt(a), beforeNull: b === null, afterNull: a === null }
  }
  const src = r.op === 'DELETE' || r.op === 'MULTISET_REMOVED' ? r.before : r.after
  const v = src && col in src ? src[col] : r.keyValues && col in r.keyValues ? r.keyValues[col] : undefined
  if (v === undefined) return { kind: 'none' }
  return { kind: 'value', text: fmt(v), isNull: v === null }
}


export const GRID_OP_LABEL: Record<GridOp, string> = {
  INSERT: '추가',
  UPDATE: '수정',
  DELETE: '삭제',
  MULTISET_REMOVED: '제거',
  MULTISET_ADDED: '추가',
}

/** 연산 열 글자. MULTISET은 E6 문구("제거된 행 n개, 추가된 행 m개")의 한 줄 몫이다. */
export function opText(r: GridRow): string {
  if (r.op === 'MULTISET_REMOVED') return `제거된 행 ${r.multiplicity ?? 1}개`
  if (r.op === 'MULTISET_ADDED') return `추가된 행 ${r.multiplicity ?? 1}개`
  return GRID_OP_LABEL[r.op]
}
