// 비교 화면(테이블 비교표 · 경로 한 줄)이 쓰는 순수 함수. 입력은 api/types.ts 모양 그대로, 계약에 없는 값은 만들지 않는다.
import type { Confidence, Firing, PreviewResult } from '../api/types'
import { mergeNetRows, splitTable } from '../components/rail/rowOps'
import { CAUSE_LABEL, causeBreakdown, displayOrder } from './summary'

/** 테이블의 행 있는 묶음들 확신도를 하나로: 모두 확실 · 모두 추론 · 섞임 · 모호/설명 안 됨 포함 */
export type ConfLevel = 'certain' | 'inferred' | 'mixed' | 'other'

export interface CompareRow {
  table: string
  schema: string
  name: string
  /** 순변경(netSummary). 다중집합 추가·제거는 추가·삭제로 센다 */
  deleted: number
  updated: number
  inserted: number
  total: number
  /** 발동 기준 문구(summary.causeBreakdown): "직접", "FK 연쇄", "트리거 2건" */
  basis: string
  /** 이 테이블을 쓴 트리거 이름(중복 없이, 표시 순번 순) */
  triggers: string[]
  /** 이 테이블로 이어진 FK 이름 */
  fks: string[]
  /** 행 있는 묶음 수와 rowCount 합(발동 기준). 같은 행을 여러 묶음이 바꾸면 순변경 합보다 크다 */
  firings: number
  firingRows: number
  conf: ConfLevel
  /** 행 이미지 없이 키만 수집(imageMode KEYS) */
  keysOnly: boolean
}

function confOf(list: readonly Confidence[]): ConfLevel {
  const s = new Set(list)
  if (s.has('ambiguous') || s.has('unexplained')) return 'other'
  if (s.size === 1) return s.has('certain') ? 'certain' : 'inferred'
  return 'mixed'
}

/** 순변경이 1행 이상인 테이블. 그래프·발동 목록과 같은 표시 순번(테이블의 첫 묶음) 순. */
export function compareRows(p: PreviewResult): CompareRow[] {
  const order = displayOrder(p.firings)
  const basis = causeBreakdown(p.firings).basisByTable
  const byTable = new Map<string, Firing[]>()
  for (const f of p.firings) {
    if (f.rowCount === 0) continue
    const list = byTable.get(f.table)
    if (list) list.push(f)
    else byTable.set(f.table, [f])
  }
  const first = (t: string) => Math.min(...(byTable.get(t) ?? []).map((f) => order.get(f.id) ?? Infinity), Infinity)

  return mergeNetRows(p.netSummary)
    .map((r) => {
      const inserted = r.inserted + r.multisetAdded
      const deleted = r.deleted + r.multisetRemoved
      const fs = [...(byTable.get(r.table) ?? [])].sort((a, b) => (order.get(a.id) ?? 0) - (order.get(b.id) ?? 0))
      const triggers = [...new Set(fs.flatMap((f) => (f.cause.kind === 'TRIGGER' || f.cause.kind === 'INSTEAD_OF' ? f.cause.triggerCandidates : [])))]
      const fks = [...new Set(fs.flatMap((f) => (f.cause.foreignKey ? [f.cause.foreignKey.name] : [])))]
      return {
        table: r.table,
        ...splitTable(r.table),
        deleted,
        updated: r.updated,
        inserted,
        total: inserted + r.updated + deleted,
        basis: basis.get(r.table) ?? '',
        triggers,
        fks,
        firings: fs.length,
        firingRows: fs.reduce((a, f) => a + f.rowCount, 0),
        conf: fs.length ? confOf(fs.map((f) => f.cause.confidence)) : 'other',
        keysOnly: fs.some((f) => f.imageMode === 'KEYS'),
      } satisfies CompareRow
    })
    .filter((x) => x.total > 0)
    .sort((a, b) => first(a.table) - first(b.table))
}

const isTrig = (f: Firing) => f.cause.kind === 'TRIGGER' || f.cause.kind === 'INSTEAD_OF'

// ---- 테이블 단위 원인 트리(D안) ----

/**
 * 테이블당 한 줄, 숫자는 순변경(합 = 합계). 들여쓰기는 원인 관계다.
 * 테이블은 행을 바꾼 묶음 가운데 가장 얕은 깊이의 묶음(기준 묶음) 자리에 놓는다(관측 순서에 기대지 않는다).
 * 같은 깊이가 여럿이면 부모 테이블 이름 → 트리거 이름 순으로 정한다. 다른 묶음의 원인은 줄을 펼쳐 경로로 본다.
 */
export type TableTreeItem =
  | { kind: 'stmt'; key: string; level: number; text: string; title: string }
  | {
      kind: 'table'
      key: string
      level: number
      row: CompareRow
      /** 부모에 잇는 원인(트리거 아래 줄은 트리거 줄이 말하므로 null) */
      cause: { text: string; title: string; certain: boolean; conf: string } | null
      /** 이 테이블을 바꾼 행 있는 묶음 가운데 기준 묶음 밖의 수(같은 행을 다시 바꾼 원인) */
      others: number
      anchorId: string
    }
  | {
      kind: 'trigger'
      key: string
      level: number
      name: string
      observed: boolean | null
      zero: number
      /** 아래에 다른 테이블이 없다 */
      noRows: boolean
      /** 이 트리거가 부모 테이블 자신을 다시 바꾼 행 수(같은 행을 두 번 바꾼 경우) */
      selfRows: number
    }

export interface TableTree {
  items: TableTreeItem[]
  /** 트리 줄에 붙지 않은 0행 쓰기 수(0행 묶음이 부른 트리거의 쓰기 등) */
  zeroElsewhere: number
  /** 0행 묶음에서만 불려 트리에 줄이 없는 트리거 이름 */
  triggersElsewhere: string[]
}

export function tableTree(p: PreviewResult): TableTree {
  const order = displayOrder(p.firings)
  const byId = new Map(p.firings.map((f) => [f.id, f]))
  const rows = compareRows(p)
  const rowBy = new Map(rows.map((r) => [r.table, r]))
  const fsBy = new Map<string, Firing[]>()
  for (const f of p.firings) {
    if (f.rowCount === 0 || !rowBy.has(f.table)) continue
    const l = fsBy.get(f.table)
    if (l) l.push(f)
    else fsBy.set(f.table, [f])
  }
  const parentOf = (f: Firing) => (f.cause.parentFiringId ? byId.get(f.cause.parentFiringId) : undefined)
  const trigName = (f: Firing) => (isTrig(f) ? (f.cause.triggerCandidates[0] ?? '알 수 없는 트리거') : '')
  /** 가장 가까운 조상 가운데 다른 테이블이면서 비교 대상(순변경 있는 테이블)인 묶음 */
  function parentTableOf(f: Firing, self: string): string | null {
    const seen = new Set<string>([f.id])
    let cur = parentOf(f)
    while (cur && !seen.has(cur.id)) {
      seen.add(cur.id)
      if (cur.table !== self && rowBy.has(cur.table)) return cur.table
      cur = parentOf(cur)
    }
    return null
  }
  const anchors = new Map<string, Firing>()
  for (const [t, fs] of fsBy) {
    const a = [...fs].sort(
      (x, y) =>
        x.depth - y.depth ||
        (parentTableOf(x, t) ?? '').localeCompare(parentTableOf(y, t) ?? '') ||
        trigName(x).localeCompare(trigName(y)) ||
        (order.get(x.id) ?? 0) - (order.get(y.id) ?? 0),
    )[0]
    anchors.set(t, a)
  }
  // 부모 키(테이블 이름 또는 "st-n") → 자식 { 테이블, 거친 트리거 }
  const kids = new Map<string, { t: string; via: string | null }[]>()
  for (const [t, a] of anchors) {
    const pt = parentTableOf(a, t)
    const key = pt ?? `st-${a.statementIndex}`
    // 부모 테이블과 사이에 트리거가 있으면 그 이름으로 묶는다(기준 묶음의 원인 기준)
    const via = pt && isTrig(a) ? trigName(a) : null
    const l = kids.get(key)
    if (l) l.push({ t, via })
    else kids.set(key, [{ t, via }])
  }
  const trigNodes = p.graph.nodes.flatMap((n) => (n.type === 'trigger' ? [n] : []))

  const out: TableTreeItem[] = []
  const seen = new Set<string>()
  let zeroShown = 0

  function emitTable(t: string, level: number, underTrigger: boolean): void {
    if (seen.has(t)) return
    seen.add(t)
    const a = anchors.get(t)!
    const link = linkOf(a)
    out.push({
      kind: 'table',
      key: t,
      level,
      row: rowBy.get(t)!,
      cause: underTrigger ? null : { text: link.text, title: link.title, certain: link.certain, conf: link.conf },
      others: (fsBy.get(t)?.length ?? 1) - 1,
      anchorId: a.id,
    })
    emitChildren(t, level + 1)
  }

  function emitChildren(key: string, level: number): void {
    const list = (kids.get(key) ?? []).slice().sort((x, y) => x.t.localeCompare(y.t))
    for (const k of list.filter((k) => !k.via)) emitTable(k.t, level, false)
    if (key.startsWith('st-')) return
    // 이 테이블의 행 있는 묶음이 부른 트리거: 자식 테이블의 트리거 + 그래프 trigger 노드(관측 안 됨 포함) + 0행 쓰기만 한 트리거
    const ids = new Set((fsBy.get(key) ?? []).map((f) => f.id))
    const names = new Set(list.filter((k) => k.via).map((k) => k.via!))
    for (const n of trigNodes) if (n.parentFiringId && ids.has(n.parentFiringId)) names.add(n.trigger)
    const zeroBy = new Map<string, number>()
    const selfBy = new Map<string, number>()
    for (const f of p.firings) {
      if (!isTrig(f) || !f.cause.parentFiringId || !ids.has(f.cause.parentFiringId)) continue
      const nm = trigName(f)
      if (f.rowCount === 0) {
        names.add(nm)
        zeroBy.set(nm, (zeroBy.get(nm) ?? 0) + 1)
      } else if (f.table === key) {
        names.add(nm)
        selfBy.set(nm, (selfBy.get(nm) ?? 0) + f.rowCount)
      }
    }
    for (const nm of [...names].sort((x, y) => x.localeCompare(y))) {
      const nodes = trigNodes.filter((n) => n.trigger === nm && n.parentFiringId && ids.has(n.parentFiringId))
      const observed = nodes.length === 0 ? null : nodes.some((n) => n.observed)
      const under = list.filter((k) => k.via === nm)
      const zero = zeroBy.get(nm) ?? 0
      zeroShown += zero
      out.push({ kind: 'trigger', key: `${key}>${nm}`, level, name: nm, observed, zero, noRows: under.length === 0, selfRows: selfBy.get(nm) ?? 0 })
      for (const k of under) emitTable(k.t, level + 1, true)
    }
  }

  const stmts = [...p.statements].sort((a, b) => a.index - b.index)
  for (const s of stmts) {
    out.push({
      kind: 'stmt',
      key: `st-${s.index}`,
      level: 0,
      text: `${s.kind} ${s.target ?? s.procedure ?? ''}`.trim(),
      title: stmts.length > 1 ? `문장 ${s.index}` : '문장',
    })
    emitChildren(`st-${s.index}`, 1)
  }
  for (const r of rows) if (!seen.has(r.table)) emitTable(r.table, 1, false) // 닿지 않은 테이블도 빠뜨리지 않는다
  const zeroAll = p.firings.filter((f) => f.rowCount === 0).length
  const rowIds = new Set([...fsBy.values()].flat().map((f) => f.id))
  const triggersElsewhere = [...new Set(trigNodes.filter((n) => !n.parentFiringId || !rowIds.has(n.parentFiringId)).map((n) => n.trigger))].sort()
  return { items: out, zeroElsewhere: Math.max(0, zeroAll - zeroShown), triggersElsewhere }
}

// ---- 경로 한 줄 ----

export type PathItem =
  | { type: 'stmt'; text: string; title: string }
  | { type: 'table'; text: string; title: string; here: boolean }
  /** conf: 확신도 글자(확실 · 추론 · 모호 · 설명 안 됨) */
  | { type: 'link'; text: string; title: string; certain: boolean; conf: string }

/** "Sales.iduSalesOrderDetail" → "iduSalesOrderDetail"(전체 이름은 title로) */
function shortName(qualified: string): string {
  return splitTable(qualified).name
}

const CONF_LABEL: Record<Confidence, string> = { certain: '확실', inferred: '추론', ambiguous: '모호', unexplained: '설명 안 됨' }

function linkOf(f: Firing): Extract<PathItem, { type: 'link' }> {
  const c = f.cause
  const certain = c.confidence === 'certain'
  const conf = CONF_LABEL[c.confidence]
  if (c.kind === 'TRIGGER' || c.kind === 'INSTEAD_OF') {
    const names = c.triggerCandidates
    const text = names.length ? `${c.kind === 'INSTEAD_OF' ? 'INSTEAD OF ' : ''}트리거 ${names.map(shortName).join(', ')}` : CAUSE_LABEL[c.kind]
    return { type: 'link', text, title: `${CAUSE_LABEL[c.kind]} ${names.join(', ')} · ${conf}`, certain, conf }
  }
  if (c.foreignKey) {
    const fk = c.foreignKey
    return { type: 'link', text: CAUSE_LABEL[c.kind], title: `${fk.name} · ${fk.action} · 일치 ${fk.matchedRows}행 · ${conf}`, certain, conf }
  }
  return { type: 'link', text: CAUSE_LABEL[c.kind], title: `${CAUSE_LABEL[c.kind]} · ${conf}`, certain, conf }
}

/**
 * 문장에서 이 테이블까지의 경로. 테이블의 행 있는 묶음마다 cause.parentFiringId 를 거슬러 올라가 한 줄씩 만들고, 같은 줄은 하나로 합친다.
 * 순서는 표시 순번(관측 순서) 순이며 실행 순서를 뜻하지 않는다.
 */
export function tablePaths(p: PreviewResult, table: string): PathItem[][] {
  const byId = new Map(p.firings.map((f) => [f.id, f]))
  const order = displayOrder(p.firings)
  const mine = p.firings
    .filter((f) => f.table === table && f.rowCount > 0)
    .sort((a, b) => (order.get(a.id) ?? 0) - (order.get(b.id) ?? 0))
  const out: PathItem[][] = []
  const seen = new Set<string>()
  for (const f of mine) {
    const chain: Firing[] = [f]
    const visited = new Set([f.id])
    let cur = f
    while (cur.cause.parentFiringId && byId.has(cur.cause.parentFiringId) && !visited.has(cur.cause.parentFiringId)) {
      cur = byId.get(cur.cause.parentFiringId)!
      visited.add(cur.id)
      chain.unshift(cur)
    }
    const s = p.statements.find((x) => x.index === chain[0].statementIndex)
    const stTitle = s ? `${s.kind} ${s.target ?? s.procedure ?? ''}`.trim() : `문장 ${chain[0].statementIndex}`
    const items: PathItem[] = [{ type: 'stmt', text: p.statements.length > 1 ? `문장 ${chain[0].statementIndex}` : '문장', title: stTitle }]
    chain.forEach((c, i) => {
      items.push(linkOf(c))
      const here = i === chain.length - 1
      items.push({ type: 'table', text: here ? '이 테이블' : shortName(c.table), title: c.table, here })
    })
    const key = items.map((x) => `${x.type}:${x.text}`).join('|')
    if (seen.has(key)) continue
    seen.add(key)
    out.push(items)
  }
  return out
}
