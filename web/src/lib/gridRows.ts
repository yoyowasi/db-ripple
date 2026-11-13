// diff 그리드용 행 모델(C3). 이벤트 행(RowChange)과 최종 차이 행(NetChange)을 같은 모양으로 바꾼다.
// 셀은 valueGetter(문자열, 복사·툴팁용) + cellRenderer(rows/cellRenderers.ts, 이전 → 이후 표시)로 그린다.
// "예상값"·"파생" 뱃지는 열 머리(rows/ColHeader.vue)에 붙는다.
import type { ColDef } from 'ag-grid-community'
import ColHeader from '../components/rows/ColHeader.vue'
import { cellRenderer, opRenderer } from '../components/rows/cellRenderers'
import { cellParts, opText } from '../components/rows/cellModel'
import type {
  NetChange,
  NetChangeKind,
  Op,
  RowChange,
  SchemaColumn,
  SchemaTable,
  Values,
  VolatileReason,
} from '../api/types'
import { VOLATILE_LABEL, keyText } from './format'

export { GRID_OP_LABEL, opText } from '../components/rows/cellModel'

export type GridOp = Op | 'MULTISET_REMOVED' | 'MULTISET_ADDED'

export interface GridRow {
  rowId: string
  table: string
  op: GridOp
  /** 최종 차이의 같은 행 개수(다중집합), 이벤트 행은 null */
  multiplicity: number | null
  keyText: string
  /** 행 키를 열 이름 → 값으로 푼 것. 이미지에 열이 없을 때(키만 수집) 값 자리를 채운다. */
  keyValues?: Values
  before: Values | null
  after: Values | null
  changed: Set<string>
  volatile: Map<string, VolatileReason>
  derived: Set<string>
  /** 이 행을 설명하는 묶음(최종 차이 보기에서 선택한 노드 강조용) */
  firingIds: string[]
  /** UPDATE의 전후 짝을 확정하지 못했다(pairing UNPAIRED) */
  unpaired?: boolean
  note: string
}

/** 열 메타(스키마)에서 비결정·계산 열을 찾는다. 스키마가 없으면 행 자체의 표시만 쓴다. */
export interface ColumnMeta {
  volatile: Map<string, VolatileReason>
  derived: Set<string>
}

export function columnMeta(table: SchemaTable | undefined): ColumnMeta {
  const volatile = new Map<string, VolatileReason>()
  const derived = new Set<string>()
  if (table) {
    for (const c of table.columns) {
      if (c.volatile) volatile.set(c.name, c.volatile)
      else if (c.isRowVersion) volatile.set(c.name, 'ROWVERSION')
      if (c.isComputed) derived.add(c.name)
    }
  }
  return { volatile, derived }
}

function keyValuesOf(k: RowChange['key']): Values | undefined {
  if (k.kind === 'ROW') return undefined
  const out: Values = {}
  k.columns.forEach((c, i) => (out[c] = k.values[i] ?? null))
  return out
}

export function eventRowToGrid(r: RowChange, table: string, meta: ColumnMeta): GridRow {
  const volatile = new Map(meta.volatile)
  for (const v of r.volatile) volatile.set(v.column, v.reason)
  const derived = new Set([...meta.derived, ...r.derived])
  const unpaired = r.operation === 'UPDATE' && r.pairing === 'UNPAIRED'
  return {
    rowId: r.id,
    table,
    op: r.operation,
    multiplicity: null,
    keyText: keyText(r.key),
    keyValues: keyValuesOf(r.key),
    before: r.before,
    after: r.after,
    changed: new Set(r.changedColumns),
    volatile,
    derived,
    firingIds: [r.firingId],
    unpaired,
    note: r.pairing === 'UNPAIRED' ? '전후 짝 미확정(삭제와 추가의 쌍)' : '',
  }
}

const NET_TO_OP: Record<NetChangeKind, GridOp> = {
  INSERTED: 'INSERT',
  UPDATED: 'UPDATE',
  DELETED: 'DELETE',
  MULTISET_REMOVED: 'MULTISET_REMOVED',
  MULTISET_ADDED: 'MULTISET_ADDED',
}

export function netRowToGrid(r: NetChange, index: number, meta: ColumnMeta): GridRow {
  return {
    rowId: `${r.table}|${r.change}|${keyText(r.key)}|${index}`,
    table: r.table,
    op: NET_TO_OP[r.change],
    multiplicity: r.multiplicity,
    keyText: keyText(r.key),
    keyValues: keyValuesOf(r.key),
    before: r.before,
    after: r.after,
    changed: new Set(r.changedColumns),
    volatile: meta.volatile,
    derived: meta.derived,
    firingIds: r.explainedBy?.firingIds ?? [],
    note: r.explainedBy?.residualId ? `잔차 ${r.explainedBy.residualId}로도 설명됨` : '',
  }
}

/** 한 셀의 표시 문자열(복사·툴팁용). UPDATE에서 바뀐 열은 "이전 → 이후"다. */
export function cellText(r: GridRow, col: string): string {
  const p = cellParts(r, col)
  if (p.kind === 'change') return `${p.before} → ${p.after}`
  return p.kind === 'value' ? p.text : ''
}

export function cellTitle(r: GridRow, col: string): string {
  const parts: string[] = [cellText(r, col)]
  const v = r.volatile.get(col)
  if (v) parts.push(`예상값: ${VOLATILE_LABEL[v]}. 원본에 적용하면 값이 달라질 수 있습니다(존재와 형식만 비교).`)
  if (r.derived.has(col)) parts.push('파생: 계산 열이라 식으로 정해집니다.')
  return parts.join('\n')
}

/** 표시할 데이터 열 순서: 한 테이블이면 스키마 열 순서, 아니면 행에 나타난 순서. 이미지에 열이 없으면 키 열을 쓴다. */
export function dataColumns(rows: GridRow[], tables: SchemaTable[]): string[] {
  const seen: string[] = []
  const set = new Set<string>()
  const add = (c: string) => {
    if (!set.has(c)) {
      set.add(c)
      seen.push(c)
    }
  }
  const tableNames = new Set(rows.map((r) => r.table))
  const hasImage = rows.some((r) => r.before || r.after)
  if (tableNames.size === 1 && hasImage) {
    const only = tables.find((t) => t.table === [...tableNames][0])
    if (only) for (const c of only.columns) add(c.name)
  }
  for (const r of rows) {
    for (const v of [r.before, r.after, r.keyValues]) if (v) for (const c of Object.keys(v)) add(c)
  }
  return seen
}

export interface ColumnTag {
  volatile?: VolatileReason
  derived: boolean
}

/** 열마다 "예상값"·"파생" 표시가 필요한지(보이는 행 중 하나라도 해당하면 켠다). */
export function columnTags(rows: readonly GridRow[], columns: readonly string[]): Map<string, ColumnTag> {
  const out = new Map<string, ColumnTag>()
  for (const col of columns) {
    const tag: ColumnTag = { derived: false }
    for (const r of rows) {
      const v = r.volatile.get(col)
      if (v && !tag.volatile) tag.volatile = v
      if (r.derived.has(col)) tag.derived = true
      if (tag.volatile && tag.derived) break
    }
    out.set(col, tag)
  }
  return out
}

export interface BuildColumnsOptions {
  showTable: boolean
  columns: string[]
  schemaColumns: Map<string, SchemaColumn>
  /** 열 머리 뱃지. 없으면 뱃지 없이 열 이름만 보인다. */
  tags?: Map<string, ColumnTag>
  /** 열 너비를 값 길이에 맞추는 데 쓴다. 없으면 160px. */
  rows?: readonly GridRow[]
}

/** 고정폭 13px 글자 기준 어림 너비. 값이 가장 긴 행과 열 머리(이름 + 뱃지) 중 큰 쪽, 110~420px. */
function columnWidth(col: string, tag: ColumnTag | undefined, rows: readonly GridRow[] | undefined): number {
  if (!rows) return 160
  let chars = 0
  for (const r of rows) chars = Math.max(chars, cellText(r, col).length)
  const head = col.length * 7.6 + (tag?.volatile ? 64 : 0) + (tag?.derived ? 48 : 0) + 28
  return Math.round(Math.min(420, Math.max(110, chars * 7.9 + 28 + (tag?.volatile ? 64 : 0), head)))
}

export function buildColumnDefs(o: BuildColumnsOptions): ColDef<GridRow>[] {
  const defs: ColDef<GridRow>[] = [
    {
      colId: '__op',
      headerName: '변경',
      pinned: 'left',
      width: o.showTable ? 150 : 130,
      sortable: false,
      valueGetter: (p) => (p.data ? opText(p.data) : ''),
      cellRenderer: opRenderer,
      cellClass: (p) => (p.data ? [`op-${p.data.op.toLowerCase()}`, 'cell-op'] : []),
      tooltipValueGetter: (p) => (p.data?.note ? `${opText(p.data)}\n${p.data.note}` : undefined),
    },
  ]
  if (o.showTable) {
    defs.push({
      colId: '__table',
      headerName: '테이블',
      pinned: 'left',
      width: 210,
      valueGetter: (p) => p.data?.table ?? '',
    })
  }
  for (const col of o.columns) {
    const meta = o.schemaColumns.get(col)
    const tag = o.tags?.get(col)
    defs.push({
      colId: `c:${col}`,
      headerName: col,
      headerComponent: ColHeader,
      headerComponentParams: {
        tip: meta ? `${col} ${meta.sqlType}${meta.isNullable ? ' NULL' : ' NOT NULL'}` : col,
        volatile: tag?.volatile,
        derived: tag?.derived,
      },
      minWidth: 90,
      width: columnWidth(col, tag, o.rows),
      valueGetter: (p) => (p.data ? cellText(p.data, col) : ''),
      cellRenderer: cellRenderer(col),
      cellClass: (p) => (p.data?.op === 'UPDATE' && p.data.changed.has(col) ? 'c-chg' : undefined),
      tooltipValueGetter: (p) => (p.data ? cellTitle(p.data, col) : undefined),
    })
  }
  return defs
}

