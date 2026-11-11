// diff 그리드용 행 모델(C3). 이벤트 행(RowChange)과 최종 차이 행(NetChange)을 같은 모양으로 바꾼다.
// 셀 표시는 AG Grid 렌더러 없이 valueGetter(문자열) + cellClass(CSS 클래스)만 쓴다.
// "예상값"·"파생" 뱃지는 CSS ::after 로 붙인다.
import type { ColDef } from 'ag-grid-community'
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

export type GridOp = Op | 'MULTISET_REMOVED' | 'MULTISET_ADDED'

export interface GridRow {
  rowId: string
  table: string
  op: GridOp
  /** 최종 차이의 같은 행 개수(다중집합), 이벤트 행은 null */
  multiplicity: number | null
  keyText: string
  before: Values | null
  after: Values | null
  changed: Set<string>
  volatile: Map<string, VolatileReason>
  derived: Set<string>
  /** 이 행을 설명하는 묶음(최종 차이 보기에서 선택한 노드 강조용) */
  firingIds: string[]
  note: string
}

export const GRID_OP_LABEL: Record<GridOp, string> = {
  INSERT: '추가',
  UPDATE: '수정',
  DELETE: '삭제',
  MULTISET_REMOVED: '제거',
  MULTISET_ADDED: '추가',
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

export function eventRowToGrid(r: RowChange, table: string, meta: ColumnMeta): GridRow {
  const volatile = new Map(meta.volatile)
  for (const v of r.volatile) volatile.set(v.column, v.reason)
  const derived = new Set([...meta.derived, ...r.derived])
  return {
    rowId: r.id,
    table,
    op: r.operation,
    multiplicity: null,
    keyText: keyText(r.key),
    before: r.before,
    after: r.after,
    changed: new Set(r.changedColumns),
    volatile,
    derived,
    firingIds: [r.firingId],
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
    before: r.before,
    after: r.after,
    changed: new Set(r.changedColumns),
    volatile: meta.volatile,
    derived: meta.derived,
    firingIds: r.explainedBy?.firingIds ?? [],
    note: r.explainedBy?.residualId ? `잔차 ${r.explainedBy.residualId}로도 설명됨` : '',
  }
}

/** 연산 열에 보일 글자. MULTISET은 E6 문구("제거된 행 n개, 추가된 행 m개")의 한 줄 몫이다. */
export function opText(r: GridRow): string {
  if (r.op === 'MULTISET_REMOVED') return `제거된 행 ${r.multiplicity ?? 1}개`
  if (r.op === 'MULTISET_ADDED') return `추가된 행 ${r.multiplicity ?? 1}개`
  return `${GRID_OP_LABEL[r.op]} ${r.op}`
}

const NULL_TEXT = 'NULL'
const fmt = (v: string | null | undefined): string => (v === null ? NULL_TEXT : (v ?? ''))

/** 한 셀의 표시 문자열. UPDATE에서 바뀐 열은 "이전 → 이후"로 보인다. */
export function cellText(r: GridRow, col: string): string {
  const useBefore = r.op === 'DELETE' || r.op === 'MULTISET_REMOVED'
  const src = useBefore ? r.before : r.after
  if (r.op === 'UPDATE' && r.changed.has(col)) {
    return `${fmt(r.before?.[col])} → ${fmt(r.after?.[col])}`
  }
  if (!src || !(col in src)) return ''
  return fmt(src[col])
}

export function cellClasses(r: GridRow, col: string): string[] {
  const cls: string[] = []
  if (r.op === 'UPDATE' && r.changed.has(col)) cls.push('cell-changed')
  if (r.volatile.has(col)) cls.push('cell-volatile')
  if (r.derived.has(col)) cls.push('cell-derived')
  const t = cellText(r, col)
  if (t === NULL_TEXT) cls.push('cell-null')
  return cls
}

export function cellTitle(r: GridRow, col: string): string {
  const parts: string[] = [cellText(r, col)]
  const v = r.volatile.get(col)
  if (v) parts.push(`예상값: ${VOLATILE_LABEL[v]}. 원본에 적용하면 값이 달라질 수 있습니다(존재와 형식만 비교).`)
  if (r.derived.has(col)) parts.push('파생: 계산 열이라 식으로 정해집니다.')
  return parts.join('\n')
}

/** 표시할 데이터 열 순서: 한 테이블이면 스키마 열 순서, 아니면 행에 나타난 순서. */
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
  if (tableNames.size === 1) {
    const only = tables.find((t) => t.table === [...tableNames][0])
    if (only) for (const c of only.columns) add(c.name)
  }
  for (const r of rows) {
    for (const v of [r.before, r.after]) if (v) for (const c of Object.keys(v)) add(c)
  }
  return seen
}

export interface BuildColumnsOptions {
  showTable: boolean
  columns: string[]
  schemaColumns: Map<string, SchemaColumn>
}

export function buildColumnDefs(o: BuildColumnsOptions): ColDef<GridRow>[] {
  const defs: ColDef<GridRow>[] = [
    {
      colId: '__op',
      headerName: '연산',
      pinned: 'left',
      width: 150,
      sortable: false,
      valueGetter: (p) => (p.data ? opText(p.data) : ''),
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
  defs.push({
    colId: '__key',
    headerName: '키',
    pinned: 'left',
    width: 180,
    valueGetter: (p) => p.data?.keyText ?? '',
    tooltipValueGetter: (p) => p.data?.keyText,
  })
  for (const col of o.columns) {
    const meta = o.schemaColumns.get(col)
    defs.push({
      colId: `c:${col}`,
      headerName: col,
      headerTooltip: meta ? `${col} ${meta.sqlType}${meta.isNullable ? ' NULL' : ' NOT NULL'}` : col,
      minWidth: 110,
      width: 150,
      valueGetter: (p) => (p.data ? cellText(p.data, col) : ''),
      cellClass: (p) => (p.data ? cellClasses(p.data, col) : []),
      tooltipValueGetter: (p) => (p.data ? cellTitle(p.data, col) : undefined),
    })
  }
  return defs
}
