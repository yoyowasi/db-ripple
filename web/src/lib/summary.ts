// 화면 여러 곳(레일 헤드라인·그래프 위 한 줄·발동 목록·탐색기 뱃지·적용 상태)이 같은 값을 쓰도록 모은 순수 함수.
// 입력은 api/types.ts 모양 그대로다. 계약에 없는 값은 만들지 않는다.
import type { Cause, Firing, NetSummaryRow, Op, PreviewResult } from '../api/types'
import type { GateResult } from './applyGate'

const n = (v: number) => v.toLocaleString('en-US')

// ---- (a) 헤드라인 ----

export interface NetTotals {
  /** 순변경이 1행 이상인 테이블 수 */
  tables: number
  /** inserted + multisetAdded */
  inserted: number
  updated: number
  /** deleted + multisetRemoved */
  deleted: number
  total: number
}

/** netSummary 합계. 키 없는 테이블의 MULTISET_ADDED/REMOVED는 추가/삭제로 센다. */
export function netTotals(rows: readonly NetSummaryRow[]): NetTotals {
  let tables = 0
  let inserted = 0
  let updated = 0
  let deleted = 0
  for (const r of rows) {
    const i = r.inserted + r.multisetAdded
    const d = r.deleted + r.multisetRemoved
    if (i + r.updated + d > 0) tables++
    inserted += i
    updated += r.updated
    deleted += d
  }
  return { tables, inserted, updated, deleted, total: inserted + updated + deleted }
}

/**
 * 레일 맨 위 헤드라인. netSummary(순변경) 기준.
 * - 미리보기 없음 → "미리보기 결과 없음"
 * - trigger_blocked → "트리거가 거부해 변경 0건", failed → "문장이 실패해 변경 0건"
 * - 한 종류만 → "테이블 3개 · 4행 삭제", 섞이면 → "테이블 4개 · 추가 1 · 수정 3"(추가·수정·삭제 순)
 * - 순변경 0행 → "변경 0건"(truncated인데 합계가 없으면 "결과가 잘려 건수를 알 수 없음")
 * truncated라도 netSummary가 있으면 같은 문구다(건수는 정확, 잘림은 따로 배너로 알린다).
 */
export function previewHeadline(p: PreviewResult | null): string {
  if (!p) return '미리보기 결과 없음'
  if (p.status === 'trigger_blocked') return '트리거가 거부해 변경 0건'
  if (p.status === 'failed') return '문장이 실패해 변경 0건'
  const t = netTotals(p.netSummary)
  if (t.total === 0) return p.status === 'truncated' ? '결과가 잘려 건수를 알 수 없음' : '변경 0건'
  const parts: [number, string][] = [
    [t.inserted, '추가'],
    [t.updated, '수정'],
    [t.deleted, '삭제'],
  ]
  const nonZero = parts.filter(([v]) => v > 0)
  const body =
    nonZero.length === 1 ? `${n(nonZero[0][0])}행 ${nonZero[0][1]}` : nonZero.map(([v, l]) => `${l} ${n(v)}`).join(' · ')
  return `테이블 ${n(t.tables)}개 · ${body}`
}

// ---- (b) 표시 순번 ----

/**
 * 묶음(firing)마다 화면 표시 순번(1부터). 그래프 노드와 발동 목록·서랍·레일이 같은 값을 쓴다.
 * 규칙: 문장(statementIndex 오름차순)에서 시작해 cause.parentFiringId 를 따라 깊이 우선으로 센다.
 * 형제 사이는 captureSeq 오름차순(관측 순서). 0행 묶음은 행 있는 묶음을 다 센 뒤 같은 순회 순서로 뒤에 붙인다.
 * 부모가 목록에 없는 묶음은 뿌리로 본다. captureSeq 자체를 순번으로 쓰지 않는다(감사 기록 순서라 자식이 먼저 온다).
 */
export function displayOrder(firings: readonly Firing[]): Map<string, number> {
  const byId = new Map(firings.map((f) => [f.id, f]))
  const children = new Map<string | null, Firing[]>()
  for (const f of firings) {
    const p = f.cause.parentFiringId
    const key = p !== null && byId.has(p) && p !== f.id ? p : null
    const list = children.get(key)
    if (list) list.push(f)
    else children.set(key, [f])
  }
  const roots = (children.get(null) ?? []).sort((a, b) => a.statementIndex - b.statementIndex || a.captureSeq - b.captureSeq)
  for (const [k, list] of children) if (k !== null) list.sort((a, b) => a.captureSeq - b.captureSeq)

  const walk: Firing[] = []
  const seen = new Set<string>()
  const visit = (f: Firing) => {
    if (seen.has(f.id)) return
    seen.add(f.id)
    walk.push(f)
    for (const c of children.get(f.id) ?? []) visit(c)
  }
  roots.forEach(visit)
  // 순환 등으로 닿지 않은 묶음도 빠뜨리지 않는다.
  ;[...firings].sort((a, b) => a.captureSeq - b.captureSeq).forEach(visit)

  const order = new Map<string, number>()
  for (const f of walk) if (f.rowCount > 0) order.set(f.id, order.size + 1)
  for (const f of walk) if (f.rowCount === 0) order.set(f.id, order.size + 1)
  return order
}

// ---- (c) 원인별 집계 ----

export type CauseKind = Cause['kind']

export const CAUSE_LABEL: Record<CauseKind, string> = {
  DIRECT: '직접',
  PROC_DIRECT: '프로시저 쓰기',
  FK_CASCADE: 'FK 연쇄',
  FK_SET_NULL: 'FK SET NULL',
  FK_SET_DEFAULT: 'FK SET DEFAULT',
  TRIGGER: '트리거',
  INSTEAD_OF: 'INSTEAD OF 트리거',
  UNEXPLAINED: '설명 안 됨',
}

const CAUSE_ORDER: CauseKind[] = ['DIRECT', 'PROC_DIRECT', 'FK_CASCADE', 'FK_SET_NULL', 'FK_SET_DEFAULT', 'INSTEAD_OF', 'TRIGGER', 'UNEXPLAINED']

export interface CauseGroup {
  kind: CauseKind
  label: string
  /** 이 원인의 행 있는 묶음 수 */
  firings: number
  /** 서로 다른 테이블 수 */
  tables: number
  /** 묶음 rowCount 합(발동 기준. 같은 행을 여러 묶음이 바꾸면 겹쳐 센다 → 순변경 합과 다를 수 있다) */
  rows: number
}

export interface CauseBreakdown {
  /** 행 있는 묶음만. CAUSE_ORDER 순(직접 → FK → INSTEAD OF → 트리거 → 설명 안 됨) */
  groups: CauseGroup[]
  /** 테이블별 "발동 기준" 문구. 예: "직접", "FK 연쇄", "트리거 2건", "직접 · 트리거 2건" */
  basisByTable: Map<string, string>
  /** 0행 묶음(표시 순번 순) */
  zeroRow: Firing[]
}

/**
 * firings 를 cause.kind 로 묶는다. 그래프 위 한 줄 요약과 레일 "발동 기준" 열이 쓴다.
 * 발동 기준 문구: 원인별 묶음 수가 1이면 이름만, 2 이상이면 "이름 n건". 트리거는 1건이어도 "트리거 1건"(목업 v6).
 */
export function causeBreakdown(firings: readonly Firing[]): CauseBreakdown {
  const order = displayOrder(firings)
  const groups = new Map<CauseKind, { firings: number; tables: Set<string>; rows: number }>()
  const perTable = new Map<string, Map<CauseKind, number>>()
  const zeroRow: Firing[] = []

  for (const f of firings) {
    if (f.rowCount === 0) {
      zeroRow.push(f)
      continue
    }
    const k = f.cause.kind
    const g = groups.get(k) ?? { firings: 0, tables: new Set<string>(), rows: 0 }
    g.firings++
    g.tables.add(f.table)
    g.rows += f.rowCount
    groups.set(k, g)
    const t = perTable.get(f.table) ?? new Map<CauseKind, number>()
    t.set(k, (t.get(k) ?? 0) + 1)
    perTable.set(f.table, t)
  }

  const basisByTable = new Map<string, string>()
  for (const [table, kinds] of perTable) {
    const text = CAUSE_ORDER.filter((k) => kinds.has(k))
      .map((k) => {
        const c = kinds.get(k)!
        return c === 1 && k !== 'TRIGGER' ? CAUSE_LABEL[k] : `${CAUSE_LABEL[k]} ${c}건`
      })
      .join(' · ')
    basisByTable.set(table, text)
  }

  zeroRow.sort((a, b) => (order.get(a.id) ?? 0) - (order.get(b.id) ?? 0))
  return {
    groups: CAUSE_ORDER.filter((k) => groups.has(k)).map((k) => {
      const g = groups.get(k)!
      return { kind: k, label: CAUSE_LABEL[k], firings: g.firings, tables: g.tables.size, rows: g.rows }
    }),
    basisByTable,
    zeroRow,
  }
}

// ---- (d) 적용 상태 한 줄 ----

export type VerdictTone = 'ok' | 'no' | 'nil'

export interface ApplyStatusLine {
  /** ok = 적용 가능, no = 막힘, nil = 미리보기 없음 */
  tone: VerdictTone
  /** 상태 문구(화면 읽기 프로그램이 바뀔 때 읽는 부분) */
  text: string
  /** "남은 n분"(올림, 0 미만은 0). 매분 바뀌므로 live 영역 밖에 둔다. 없으면 null */
  remain: string | null
}

/**
 * evaluateApplyGate 결과를 한 줄로. 켜지면 text "적용 가능" + remain "남은 n분",
 * 꺼지면 "적용 불가 · <첫 이유의 첫 문장>". 이유 전체 목록은 GateResult.reasons 를 그대로 쓴다.
 */
export function applyStatusLine(gate: GateResult): ApplyStatusLine {
  if (gate.enabled) {
    const min = gate.remainingMs === null ? null : Math.max(0, Math.ceil(gate.remainingMs / 60000))
    return { tone: 'ok', text: '적용 가능', remain: min === null ? null : `남은 ${min}분` }
  }
  const first = gate.reasons[0] ?? ''
  const sentence = first.split(/(?<=\.)\s/)[0].replace(/\.$/, '')
  return { tone: gate.remainingMs === null ? 'nil' : 'no', text: `적용 불가 · ${sentence}`, remain: null }
}

// ---- 탐색기 뱃지 ----

export type BadgeKind = 'd' | 'u' | 'i' | 'x' | 'k'

export interface TableBadge {
  /** d 삭제 · u 수정 · i 추가 · x 거부/실패 · k 키만 수집(imageMode KEYS) */
  kind: BadgeKind
  text: string
  /** 화면 읽기 프로그램용 덧말(없으면 빈 문자열) */
  srText: string
}

const OP_KIND: Record<Op, BadgeKind> = { DELETE: 'd', UPDATE: 'u', INSERT: 'i' }
const OP_TEXT: Record<Op, string> = { DELETE: '삭제', UPDATE: '수정', INSERT: '추가' }

/**
 * 마지막 미리보기에 걸린 테이블별 뱃지(키 = "스키마.테이블").
 * netSummary에서 한 종류만 있으면 "삭제 1", 섞이면 "변경 n". 키만 수집한 테이블은 k.
 * 거부·실패한 문장의 대상 테이블은 x("거부"/"실패").
 */
export function tableBadges(p: PreviewResult | null): Map<string, TableBadge> {
  const out = new Map<string, TableBadge>()
  if (!p) return out
  const keysOnly = new Set(p.firings.filter((f) => f.imageMode === 'KEYS').map((f) => f.table))
  for (const r of p.netSummary) {
    const t = netTotals([r])
    if (t.total === 0) continue
    const kinds: [number, Op][] = [
      [t.inserted, 'INSERT'],
      [t.updated, 'UPDATE'],
      [t.deleted, 'DELETE'],
    ]
    const nz = kinds.filter(([v]) => v > 0)
    const one = nz.length === 1 ? nz[0] : null
    const text = one ? `${OP_TEXT[one[1]]} ${n(one[0])}` : `변경 ${n(t.total)}`
    const k = keysOnly.has(r.table)
    out.set(r.table, { kind: k ? 'k' : one ? OP_KIND[one[1]] : 'u', text, srText: k ? ' (키만 수집)' : '' })
  }
  for (const s of p.statements) {
    if (s.status !== 'trigger_blocked' && s.status !== 'failed') continue
    if (s.target === null) continue // EXEC는 대상 테이블이 없다(안쪽 쓰기 테이블은 netSummary·묶음으로 나온다)
    out.set(s.target, { kind: 'x', text: s.status === 'trigger_blocked' ? '거부' : '실패', srText: '' })
  }
  return out
}
