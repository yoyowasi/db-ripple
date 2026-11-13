// 레일 표 공용 도우미(순수 함수). netSummary 한 줄 → 표시 문구.
import type { NetSummaryRow } from '../../api/types'
import { formatCount } from '../../lib/format'

/** 같은 테이블이 여러 줄로 오면 합친다(레일 표는 테이블당 한 줄). */
export function mergeNetRows(rows: readonly NetSummaryRow[]): NetSummaryRow[] {
  const by = new Map<string, NetSummaryRow>()
  for (const r of rows) {
    const m = by.get(r.table)
    if (!m) by.set(r.table, { ...r })
    else {
      m.inserted += r.inserted
      m.updated += r.updated
      m.deleted += r.deleted
      m.multisetAdded += r.multisetAdded
      m.multisetRemoved += r.multisetRemoved
    }
  }
  return [...by.values()]
}

/** "삭제" · "수정" · "추가", 섞이면 "추가 1 · 수정 2 · 삭제 3". 키 없는 테이블의 다중집합 추가·제거는 추가·삭제로 센다. */
export function rowOpText(r: NetSummaryRow): string {
  const parts: [number, string][] = [
    [r.inserted + r.multisetAdded, '추가'],
    [r.updated, '수정'],
    [r.deleted + r.multisetRemoved, '삭제'],
  ]
  const nz = parts.filter(([n]) => n > 0)
  if (nz.length === 1) return nz[0][1]
  return nz.map(([n, l]) => `${l} ${formatCount(n)}`).join(' · ')
}

/** "Sales.SalesOrderHeader" → { schema: "Sales", name: "SalesOrderHeader" } */
export function splitTable(table: string): { schema: string; name: string } {
  const i = table.lastIndexOf('.')
  return i < 0 ? { schema: '', name: table } : { schema: table.slice(0, i), name: table.slice(i + 1) }
}
