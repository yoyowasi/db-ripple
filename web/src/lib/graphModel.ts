// PreviewResult.graph → Vue Flow 노드·간선(목업 v6). 표시 글자·크기·경로 문장은 여기서 계약 값으로만 만든다.
// 플래그(§6.3, Q15): zeroRows = rowCount == 0, partial = imageMode != FULL, auditNotLast = auditOrder == NONE
import type { Edge, Node } from '@vue-flow/core'
import type { Firing, GraphDto, GraphEdge, GraphNode, Op } from '../api/types'
import { OP_LABEL, RESIDUAL_CATEGORY_LABEL, errorHead, execResultText } from './format'
import { layoutGraph, monoW, textW } from './graphLayout'
import { EDGE_TONE, FK_ACTION_TEXT, KIND_SHORT, edgeLabel, edgeTitle } from './graphStyle'
import type { EdgeTone } from './graphStyle'
import { CAUSE_LABEL, causeBreakdown } from './summary'
import type { CauseKind } from './summary'

export interface NodeFlags {
  zeroRows: boolean
  partial: boolean
  auditNotLast: boolean
}

export type OpTone = 'del' | 'upd' | 'add' | 'zero'
const OP_TONE: Record<Op, OpTone> = { DELETE: 'del', UPDATE: 'upd', INSERT: 'add' }

/** 노드 종류별 표시 글자(템플릿은 그대로 그린다) */
export type NodeView =
  | { kind: 'statement'; verb: string; target: string; targetShort: string; line2: string }
  /** EXEC 문장: "EXEC 스키마.이름" + 반환 값·결과 집합 + 보조 줄(안쪽 호출·관측 안 된 쓰기 대상) */
  | { kind: 'procedureCall'; target: string; targetShort: string; line2: string; calls: string; unobserved: string }
  | { kind: 'firing'; tone: OpTone; result: string; extra: string; schema: string; name: string; keysOnly: boolean }
  | { kind: 'trigger'; name: string; observed: boolean }
  | { kind: 'error'; title: string; sub: string }
  | { kind: 'residual'; title: string; sub: string }
  | { kind: 'group'; title: string; sub: string }

export interface RippleNodeData {
  node: GraphNode
  firing: Firing | null
  flags: NodeFlags
  view: NodeView
  /** 화면 표시 순번(묶음만) */
  order: number | null
  /** "왜 바뀌었나" 경로 문장(title·aria-label) */
  sentence: string
  width: number
  /** 상자 높이(0행 칩 줄은 뺀 값) */
  boxHeight: number
  /** 이 노드가 쓴 0행 묶음 수(0이면 칩 없음) */
  zeroChip: number
}

export interface RippleEdgeData {
  edge: GraphEdge
  label: string
  short: string
  title: string
  /** 첫 그리기 순서(문장에서 몇 번째 단인가, 1부터) */
  rank: number
}

export interface FlowModel {
  nodes: Node<RippleNodeData>[]
  edges: Edge<RippleEdgeData>[]
  /** 보이는 간선만. 노드 id → 들어오는 간선 */
  incoming: Map<string, Edge<RippleEdgeData>[]>
  /** 지금 숨긴 0행 묶음 수(펼쳤으면 0) */
  hiddenZeroRows: number
  /** 접을 수 있는 0행 묶음 수(길목에 놓여 늘 보이는 0행 묶음은 뺀다) */
  collapsibleZeroRows: number
  totalNodes: number
  maxRank: number
  layoutMs: number
}

const fmtN = (v: number) => v.toLocaleString('en-US')

/** "Sales.SalesOrderHeader" → ["Sales.", "SalesOrderHeader"] */
export function splitTable(t: string): [string, string] {
  const i = t.indexOf('.')
  return i < 0 ? ['', t] : [t.slice(0, i + 1), t.slice(i + 1)]
}
const shortName = (t: string) => splitTable(t)[1]

const STATUS_TEXT: Record<string, string> = { failed: '실패', trigger_blocked: '거부됨', not_run: '실행 안 됨' }
const VERB_RE = /^\s*(DELETE(?:\s+FROM)?|UPDATE|INSERT(?:\s+INTO)?|MERGE(?:\s+INTO)?)\s+([^\s(;]+)/i

function statementView(n: Extract<GraphNode, { type: 'statement' }>): NodeView {
  const m = VERB_RE.exec(n.label)
  const where = /\bWHERE\s+([\s\S]*)$/i.exec(n.label)?.[1].replace(/\s+/g, ' ').replace(/;\s*$/, '')
  const rows = n.rowsAffected === null ? null : `${fmtN(n.rowsAffected)}행`
  const line2 = [`문장 ${n.statementIndex}`, where ? `WHERE ${where}` : rows, STATUS_TEXT[n.status]].filter(Boolean).join(' · ')
  if (!m) {
    const t = n.label.replace(/\s+/g, ' ')
    return { kind: 'statement', verb: '', target: t, targetShort: t, line2 }
  }
  return { kind: 'statement', verb: m[1].split(/\s+/)[0].toUpperCase(), target: m[2], targetShort: shortName(m[2]), line2 }
}

function procedureView(n: Extract<GraphNode, { type: 'procedureCall' }>): NodeView {
  const line2 = [`문장 ${n.statementIndex}`, execResultText(n), STATUS_TEXT[n.status]].filter(Boolean).join(' · ')
  return {
    kind: 'procedureCall',
    target: n.procedure,
    targetShort: shortName(n.procedure),
    line2,
    calls: n.expandedCalls.join(', '),
    // 정적으로 쓰는 테이블인데 이번 실행에서 묶음이 관측되지 않은 것(점선 표시용)
    unobserved: n.unobservedWriteTargets.join(', '),
  }
}

/** EXEC 노드의 화면 낭독 문구: 상태·반환 값·안쪽 호출·관측 안 된 쓰기 대상까지(화면에서는 말줄임·툴팁으로만 보이는 정보). */
function procedureAria(sentence: string, v: Extract<NodeView, { kind: 'procedureCall' }>): string {
  return [sentence, v.line2, v.calls && `안쪽 호출 ${v.calls}`, v.unobserved && `관측 안 됨: ${v.unobserved}`].filter(Boolean).join('. ')
}

function firingView(n: Extract<GraphNode, { type: 'firing' }>, f: Firing | null): NodeView {
  const zero = n.rowCount === 0
  const [schema, name] = splitTable(n.table)
  return {
    kind: 'firing',
    tone: zero ? 'zero' : OP_TONE[n.operation],
    result: zero ? '변경 없음' : `${fmtN(n.rowCount)}행 ${OP_LABEL[n.operation]}`,
    extra: zero ? `${OP_LABEL[n.operation]} 0행${f?.cause.confidence === 'inferred' ? ' · 추론' : ''}` : '',
    schema,
    name,
    keysOnly: f?.imageMode === 'KEYS',
  }
}

function viewOf(n: GraphNode, f: Firing | null, blockedByTrigger: boolean): NodeView {
  switch (n.type) {
    case 'statement':
      return statementView(n)
    case 'procedureCall':
      return procedureView(n)
    case 'firing':
      return firingView(n, f)
    case 'trigger':
      return { kind: 'trigger', name: shortName(n.trigger), observed: n.observed }
    case 'error':
      return {
        kind: 'error',
        title: errorHead(n),
        // 오류 번호 0 = SQL Server 오류가 아닌 도구 판정(예: "프로시저가 트랜잭션을 롤백함")이라 번호 대신 그 메시지를 보인다.
        sub:
          n.errorNumber === 0
            ? n.message
            : [blockedByTrigger ? '트리거가 거부함' : null, n.isToolFault ? '도구 오류' : '도구 오류 아님'].filter(Boolean).join(' · '),
      }
    case 'residual':
      return { kind: 'residual', title: '설명되지 않은 변경', sub: `${n.table} · ${RESIDUAL_CATEGORY_LABEL[n.category]}` }
    case 'group':
      return { kind: 'group', title: `묶음 ${n.members.length}개 · ${fmtN(n.rowCount)}행`, sub: '같은 테이블·연산·깊이로 접음' }
  }
}

/** 노드 크기 어림(목업 v6 치수). 폭은 글자 길이로, 넘치면 말줄임. compact는 축소 표시용(부가 줄·스키마·순번 없음). */
function sizeOf(v: NodeView, zeroChip: number, compact: boolean): { w: number; h: number } {
  switch (v.kind) {
    case 'statement': {
      const l1 = monoW(`${v.verb} ${compact ? v.targetShort : v.target}`, 13)
      if (compact) return { w: Math.min(320, Math.max(150, 44 + l1 + 14)), h: 44 }
      const l2 = textW(v.line2, 12)
      const w = Math.min(400, Math.max(200, 48 + Math.max(l1, l2) + 22))
      return { w, h: l2 > w - 70 ? 66 : 50 }
    }
    case 'procedureCall': {
      const l1 = monoW(`EXEC ${compact ? v.targetShort : v.target}`, 13)
      if (compact) return { w: Math.min(320, Math.max(150, 44 + l1 + 14)), h: 44 }
      const l2 = textW(v.line2, 12)
      // 보조 줄(작은 글자): 안쪽 호출 · 관측 안 됨 + 테이블 이름
      const l3 = v.calls ? textW('안쪽 호출 ', 12) + monoW(v.calls, 12) : 0
      const l4 = v.unobserved ? 8 + textW('관측 안 됨', 12) + 14 + monoW(v.unobserved, 12) : 0
      const w = Math.min(440, Math.max(210, 48 + Math.max(l1, l2, l3, l4) + 22))
      return { w, h: 50 + (v.calls ? 17 : 0) + (v.unobserved ? 19 : 0) + (l2 > w - 70 ? 16 : 0) }
    }
    case 'firing': {
      const keys = v.keysOnly ? 8 + textW('키만', 12) + 14 : 0
      if (compact) return { w: Math.min(320, Math.max(140, 40 + textW(v.result, 17, true) + keys + 12, monoW(v.name, 13) + 24)), h: 60 }
      const r1 = 42 + textW(v.result, 16, true) + (v.extra ? 8 + textW(v.extra, 12) : 0) + keys + 44
      const r2 = monoW(v.schema, 12) + monoW(v.name, 13) + 30
      return { w: Math.min(420, Math.max(v.tone === 'zero' ? 240 : 210, r1, r2)), h: 68 }
    }
    case 'trigger': {
      const w = 14 + 16 + 8 + monoW(v.name, 12) + (v.observed || compact ? 0 : 8 + textW('추정', 12)) + 16
      return { w: Math.max(w, zeroChip ? 176 : 0), h: 36 }
    }
    default:
      return compact ? { w: 200, h: 60 } : { w: 250, h: 68 }
  }
}

/** 노드 하나를 짧게(경로 문장 조각) */
function nodePhrase(n: GraphNode): string {
  switch (n.type) {
    case 'statement':
      return '문장'
    case 'procedureCall':
      return `EXEC ${n.procedure}`
    case 'firing':
      return `${shortName(n.table)} ${n.rowCount ? `${fmtN(n.rowCount)}행 ${OP_LABEL[n.operation]}` : '변경 없음(0행)'}`
    case 'trigger':
      return `${shortName(n.trigger)}${n.observed ? '' : '(추정)'}`
    case 'error':
      return errorHead(n)
    case 'residual':
      return `설명되지 않은 변경 ${shortName(n.table)}`
    case 'group':
      return `접힌 묶음 ${n.members.length}개`
  }
}

function edgePhrase(e: GraphEdge): string | null {
  const inf = e.confidence === 'inferred' ? '(추론)' : e.confidence === 'ambiguous' ? '(모호)' : ''
  switch (e.kind) {
    case 'DIRECT':
      return null
    case 'PROC_DIRECT':
      return `프로시저가 직접 씀${inf}`
    case 'FK':
      return `FK ${e.action ? FK_ACTION_TEXT[e.action] : ''}`.trim() + inf
    case 'FIRES':
      return `트리거 발동${inf}`
    case 'WRITES':
      return `쓰기${inf}`
    case 'REPLACES':
      return `INSTEAD OF${inf}`
    case 'BLOCKED':
      return '거부'
    case 'RESIDUAL':
      return '후보 원인'
  }
}

export function buildFlow(
  graph: GraphDto,
  firings: readonly Firing[],
  showZeroRows: boolean,
  order: ReadonlyMap<string, number>,
  /** 축소 표시용 배치(좁은 노드·좁은 단 간격) */
  compact = false,
): FlowModel {
  const firingById = new Map(firings.map((f) => [f.id, f]))
  const byId = new Map(graph.nodes.map((n) => [n.id, n]))
  const inAll = new Map<string, GraphEdge[]>()
  for (const e of graph.edges) {
    if (!byId.has(e.source) || !byId.has(e.target)) continue
    const l = inAll.get(e.target)
    if (l) l.push(e)
    else inAll.set(e.target, [e])
  }

  // E23: 0행 묶음은 기본으로 숨긴다. 다만 "행이 있는 노드로 이어지는 길"에 놓인 0행 묶음·트리거는 숨기지 않는다
  // (숨기면 그 뒤 노드가 문장과 끊겨 떠 버린다). 숨긴 노드에서만 들어오는 트리거(관측 안 된 것 등)는 함께 숨긴다.
  const zero = new Set(graph.nodes.filter((n) => n.type === 'firing' && n.rowCount === 0).map((n) => n.id))
  const needed = new Set<string>()
  const stack = graph.nodes.filter((n) => !zero.has(n.id) && n.type !== 'trigger').map((n) => n.id)
  while (stack.length) {
    const id = stack.pop()!
    if (needed.has(id)) continue
    needed.add(id)
    for (const e of inAll.get(id) ?? []) stack.push(e.source)
  }
  const wouldHide = new Set([...zero].filter((id) => !needed.has(id)))
  for (let changed = true; changed; ) {
    changed = false
    for (const n of graph.nodes) {
      if (n.type !== 'trigger' || needed.has(n.id) || wouldHide.has(n.id)) continue
      const ins = inAll.get(n.id)
      if (ins?.length && ins.every((e) => wouldHide.has(e.source))) {
        wouldHide.add(n.id)
        changed = true
      }
    }
  }
  const hidden = showZeroRows ? new Set<string>() : wouldHide
  /** 접을 수 있는 0행 묶음(숨김 대상이 된 것) */
  const collapsible = [...zero].filter((id) => wouldHide.has(id))

  // 0행 칩: 접을 수 있는 0행 묶음마다 가장 가까운 "숨기지 않는" 조상에 센다(펼친 뒤에도 같은 자리에 "접기"로 남는다).
  const chip = new Map<string, number>()
  for (const id of collapsible) {
    const seen = new Set<string>([id])
    let frontier = [id]
    let owner: string | null = null
    while (frontier.length && !owner) {
      const next: string[] = []
      for (const x of frontier) {
        for (const e of inAll.get(x) ?? []) {
          if (seen.has(e.source)) continue
          seen.add(e.source)
          if (!wouldHide.has(e.source)) {
            owner = e.source
            break
          }
          next.push(e.source)
        }
        if (owner) break
      }
      frontier = next
    }
    if (owner) chip.set(owner, (chip.get(owner) ?? 0) + 1)
  }

  const visible = graph.nodes.filter((n) => !hidden.has(n.id))
  const visibleEdges = graph.edges.filter((e) => byId.has(e.source) && byId.has(e.target) && !hidden.has(e.source) && !hidden.has(e.target))
  const incoming = new Map<string, GraphEdge[]>()
  const outgoing = new Map<string, GraphEdge[]>()
  for (const e of visibleEdges) {
    ;(incoming.get(e.target) ?? incoming.set(e.target, []).get(e.target)!).push(e)
    ;(outgoing.get(e.source) ?? outgoing.set(e.source, []).get(e.source)!).push(e)
  }

  // 단(rank): 들어오는 간선이 없는 노드에서 너비 우선
  const rank = new Map<string, number>()
  let frontier = visible.filter((n) => !incoming.has(n.id)).map((n) => n.id)
  frontier.forEach((id) => rank.set(id, 0))
  while (frontier.length) {
    const next: string[] = []
    for (const id of frontier) {
      for (const e of outgoing.get(id) ?? []) {
        if (rank.has(e.target)) continue
        rank.set(e.target, rank.get(id)! + 1)
        next.push(e.target)
      }
    }
    frontier = next
  }

  // 경로 문장: 첫 번째로 들어오는 간선을 따라 문장까지 거슬러 올라간다.
  const sentenceCache = new Map<string, string>()
  const sentence = (id: string): string => {
    const hit = sentenceCache.get(id)
    if (hit) return hit
    const parts: string[] = []
    const seen = new Set<string>()
    let cur: string | undefined = id
    while (cur && !seen.has(cur)) {
      seen.add(cur)
      const n = byId.get(cur)!
      parts.unshift(nodePhrase(n))
      const e: GraphEdge | undefined = incoming.get(cur)?.[0]
      if (!e) break
      const ep = edgePhrase(e)
      if (ep) parts.unshift(ep)
      cur = e.source
    }
    const s = parts.join(' → ')
    sentenceCache.set(id, s)
    return s
  }

  const orderOf = (n: GraphNode) => (n.type === 'firing' ? (order.get(n.firingId) ?? null) : null)
  const sortKey = (n: GraphNode) => (n.type === 'statement' || n.type === 'procedureCall' ? -1e9 + n.statementIndex : (orderOf(n) ?? 1e6))
  const sized = visible
    .map((n) => {
      const firing = n.type === 'firing' ? (firingById.get(n.firingId) ?? null) : null
      const blockedByTrigger =
        n.type === 'error' &&
        (incoming.get(n.id) ?? []).some((e) => {
          const s = byId.get(e.source)
          return e.kind === 'BLOCKED' && (s?.type === 'statement' || s?.type === 'procedureCall') && s.status === 'trigger_blocked'
        })
      const view = viewOf(n, firing, blockedByTrigger)
      const zeroChip = chip.get(n.id) ?? 0
      const { w, h } = sizeOf(view, zeroChip, compact)
      return { n, firing, view, zeroChip, w, h, total: h + (zeroChip ? 34 : 0) }
    })
    .sort((a, b) => sortKey(a.n) - sortKey(b.n))

  const layout = layoutGraph({
    nodes: sized.map((s) => ({ id: s.n.id, width: s.w, height: s.total })),
    edges: visibleEdges,
    ranksep: compact ? 66 : 104,
  })

  const nodes: Node<RippleNodeData>[] = sized.map(({ n, firing, view, zeroChip, w, h }) => {
    const flags: NodeFlags = {
      zeroRows: n.type === 'firing' && n.rowCount === 0,
      partial: firing !== null && firing.imageMode !== 'FULL',
      auditNotLast: firing !== null && firing.auditOrder === 'NONE',
    }
    const ord = orderOf(n)
    const s = sentence(n.id)
    return {
      id: n.id,
      type: 'ripple',
      position: layout.positions.get(n.id) ?? { x: 0, y: 0 },
      data: { node: n, firing, flags, view, order: ord, sentence: s, width: w, boxHeight: h, zeroChip },
      ariaLabel: ord !== null ? `${s} (표시 순번 ${ord})` : view.kind === 'procedureCall' ? procedureAria(s, view) : s,
      draggable: false,
      connectable: false,
      selectable: false,
    }
  })

  let maxRank = 1
  const edges: Edge<RippleEdgeData>[] = visibleEdges.map((e) => {
    const r = rank.get(e.target) ?? 1
    maxRank = Math.max(maxRank, r)
    return {
      id: e.id,
      source: e.source,
      target: e.target,
      type: 'ripple',
      data: { edge: e, label: edgeLabel(e), short: KIND_SHORT[e.kind], title: edgeTitle(e), rank: r },
      focusable: false,
      selectable: false,
    }
  })

  const incomingFlow = new Map<string, Edge<RippleEdgeData>[]>()
  for (const e of edges) (incomingFlow.get(e.target) ?? incomingFlow.set(e.target, []).get(e.target)!).push(e)

  return {
    nodes,
    edges,
    incoming: incomingFlow,
    hiddenZeroRows: showZeroRows ? 0 : collapsible.length,
    collapsibleZeroRows: collapsible.length,
    totalNodes: graph.nodes.length,
    maxRank,
    layoutMs: layout.ms,
  }
}

/** 노드에서 문장까지 거슬러 올라가는 모든 경로의 노드·간선 */
export function ancestorsOf(ids: Iterable<string>, incoming: ReadonlyMap<string, Edge<RippleEdgeData>[]>) {
  const nodes = new Set<string>()
  const edges = new Set<string>()
  const stack = [...ids]
  while (stack.length) {
    const id = stack.pop()!
    if (nodes.has(id)) continue
    nodes.add(id)
    for (const e of incoming.get(id) ?? []) {
      edges.add(e.id)
      stack.push(e.source)
    }
  }
  return { nodes, edges }
}

// ---- 그래프 위 한 줄 요약 ----

const SUMMARY_LABEL: Partial<Record<CauseKind, string>> = { TRIGGER: '트리거 쓰기' }
const CAUSE_ORDER: CauseKind[] = ['DIRECT', 'PROC_DIRECT', 'FK_CASCADE', 'FK_SET_NULL', 'FK_SET_DEFAULT', 'INSTEAD_OF', 'TRIGGER', 'UNEXPLAINED']

/**
 * 예: "문장 1 → 직접 1개 테이블(1행) → FK 연쇄 2개 테이블(3행) → 트리거 쓰기 2개 테이블(변경 없음 · 0행 쓰기 2건)"
 * 거부: "문장 1 → 트리거가 거부(오류 3609) → 변경 0행". 숫자는 firings·graph에서만 센다.
 */
export function summaryLine(graph: GraphDto, firings: readonly Firing[]): string {
  const stmts = graph.nodes.filter((n) => n.type === 'statement' || n.type === 'procedureCall')
  const head = `문장 ${stmts.length}`
  const errs = graph.nodes.flatMap((n) => (n.type === 'error' && n.errorNumber !== 0 ? [n.errorNumber] : []))
  const errText = errs.length ? `(오류 ${errs.join(', ')})` : ''
  if (stmts.some((n) => n.status === 'trigger_blocked')) return `${head} → 트리거가 거부${errText} → 변경 0행`
  if (firings.length === 0 && stmts.some((n) => n.status === 'failed')) return `${head} → 실패${errText} → 변경 0행`

  const b = causeBreakdown(firings)
  const groups = new Map(b.groups.map((g) => [g.kind, g]))
  const parts = [head]
  for (const kind of CAUSE_ORDER) {
    const g = groups.get(kind)
    const zeros = b.zeroRow.filter((f) => f.cause.kind === kind)
    if (!g && zeros.length === 0) continue
    const tables = new Set([...firings.filter((f) => f.cause.kind === kind).map((f) => f.table)]).size
    const label = SUMMARY_LABEL[kind] ?? CAUSE_LABEL[kind]
    const rows = g ? `${kind === 'TRIGGER' ? '변경 ' : ''}${fmtN(g.rows)}행` : '변경 없음'
    const z = zeros.length ? `${g ? ', ' : ' · '}0행 쓰기 ${zeros.length}건` : ''
    parts.push(`${label} ${tables}개 테이블(${rows}${z})`)
  }
  if (parts.length === 1) parts.push('변경 0행')
  return parts.join(' → ')
}

// ---- 범례: 그 그래프에 있는 종류만 ----

export interface LegendPresence {
  confidences: GraphEdge['confidence'][]
  tones: EdgeTone[]
  ops: OpTone[]
  hasOrder: boolean
  statement: boolean
  /** EXEC 프로시저 호출 노드가 있다 */
  procedure: boolean
  /** 호출 노드에 "관측 안 됨" 쓰기 대상이 있다 */
  unobservedWrite: boolean
  trigger: boolean
  unobserved: boolean
  error: boolean
  residual: boolean
  group: boolean
  keysOnly: boolean
}

const CONF_ORDER: GraphEdge['confidence'][] = ['certain', 'inferred', 'ambiguous', 'unexplained']
const TONE_ORDER: EdgeTone[] = ['direct', 'call', 'fk', 'fires', 'blocked', 'residual']
const OP_ORDER: OpTone[] = ['del', 'upd', 'add', 'zero']

export function legendOf(m: FlowModel): LegendPresence {
  const conf = new Set(m.edges.map((e) => e.data!.edge.confidence))
  const tones = new Set(m.edges.map((e) => EDGE_TONE[e.data!.edge.kind]))
  const views = m.nodes.map((n) => n.data!.view)
  const ops = new Set(views.flatMap((v) => (v.kind === 'firing' ? [v.tone] : [])))
  return {
    confidences: CONF_ORDER.filter((c) => conf.has(c)),
    tones: TONE_ORDER.filter((t) => tones.has(t)),
    ops: OP_ORDER.filter((o) => ops.has(o)),
    hasOrder: m.nodes.some((n) => n.data!.order !== null),
    statement: views.some((v) => v.kind === 'statement'),
    procedure: views.some((v) => v.kind === 'procedureCall'),
    unobservedWrite: views.some((v) => v.kind === 'procedureCall' && v.unobserved !== ''),
    trigger: views.some((v) => v.kind === 'trigger'),
    unobserved: views.some((v) => v.kind === 'trigger' && !v.observed),
    error: views.some((v) => v.kind === 'error'),
    residual: views.some((v) => v.kind === 'residual'),
    group: views.some((v) => v.kind === 'group'),
    keysOnly: views.some((v) => v.kind === 'firing' && v.keysOnly),
  }
}
