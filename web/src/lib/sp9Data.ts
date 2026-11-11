// SP9(개발용 측정 화면)가 쓰는 합성 데이터. 서버 없이 그래프 노드 N개와 그리드 N행을 만든다.
import type { Confidence, Firing, GraphDto, GraphEdge, GraphNode, Op, Values, VolatileReason } from '../api/types'
import type { GridRow } from './gridRows'

// 결정적 의사 난수(같은 크기를 반복 측정할 때 같은 모양이 나오게 한다).
function lcg(seed: number): () => number {
  let s = seed >>> 0
  return () => {
    s = (Math.imul(s, 1664525) + 1013904223) >>> 0
    return s / 0x100000000
  }
}

const OPS: Op[] = ['INSERT', 'UPDATE', 'DELETE']
const CONF: Confidence[] = ['certain', 'inferred', 'inferred', 'ambiguous', 'unexplained']

export interface SyntheticGraph {
  graph: GraphDto
  firings: Firing[]
}

/**
 * 문장 1개에서 시작해 묶음 → 트리거 → 묶음(깊이 +1)으로 퍼지는 트리를 만든다. 노드 수가 target을 넘지 않게 멈춘다.
 * 묶음의 10%는 0행, 8%는 KEYS(일부만 수집), 5%는 감사 순서 NONE, 트리거의 15%는 관측 안 됨이다.
 */
export function makeSyntheticGraph(target: number): SyntheticGraph {
  const rnd = lcg(9)
  const nodes: GraphNode[] = []
  const edges: GraphEdge[] = []
  const firings: Firing[] = []
  let eid = 0
  let seq = 1000

  const edge = (source: string, to: string, kind: GraphEdge['kind'], conf: Confidence, observedOnly = false): void => {
    edges.push({
      id: `e${++eid}`,
      source,
      target: to,
      kind,
      confidence: conf,
      orderGuarantee: observedOnly ? 'observed_only' : 'guaranteed',
      evidence: ['depth'],
      fk: kind === 'FK' ? `FK_Synth_${eid}` : null,
      action: kind === 'FK' ? 'CASCADE' : null,
      matchedRows: kind === 'FK' ? Math.floor(rnd() * 20) : null,
    })
  }

  nodes.push({ type: 'statement', id: 'st-1', statementIndex: 1, label: 'UPDATE Synth.Root SET Qty = Qty + 1 …', rowsAffected: 1, status: 'ok' })

  // 너비 우선으로 퍼뜨린다. frontier: 다음에 자식을 달 묶음 노드
  const frontier: { id: string; depth: number }[] = []
  const addFiring = (parent: string, parentKind: 'statement' | 'trigger', depth: number): void => {
    const id = `f-${++seq}`
    const rowCount = rnd() < 0.1 ? 0 : 1 + Math.floor(rnd() * 12)
    const op = OPS[Math.floor(rnd() * 3)]
    const table = `Synth.T${String(Math.floor(rnd() * 40)).padStart(2, '0')}`
    nodes.push({ type: 'firing', id, firingId: id, table, operation: op, rowCount, depth, captureSeq: seq })
    firings.push({
      id,
      captureSeq: seq,
      statementIndex: 1,
      depth,
      table,
      operation: op,
      rowCount,
      imageMode: rnd() < 0.08 ? 'KEYS' : 'FULL',
      auditOrder: rnd() < 0.05 ? 'NONE' : 'LAST',
      cause: {
        kind: parentKind === 'statement' ? 'DIRECT' : 'TRIGGER',
        confidence: parentKind === 'statement' ? 'certain' : CONF[Math.floor(rnd() * CONF.length)],
        parentFiringId: null,
        foreignKey: null,
        triggerCandidates: [],
        evidence: ['depth'],
      },
      orderGuarantee: 'guaranteed',
    })
    edge(parent, id, parentKind === 'statement' ? 'DIRECT' : 'WRITES', parentKind === 'statement' ? 'certain' : CONF[Math.floor(rnd() * CONF.length)], rnd() < 0.3)
    frontier.push({ id, depth })
  }

  addFiring('st-1', 'statement', 1)

  let tn = 0
  while (nodes.length < target && frontier.length > 0) {
    const f = frontier.shift()!
    if (nodes.length + 1 >= target) break
    const tid = `t-${++tn}`
    nodes.push({ type: 'trigger', id: tid, trigger: `Synth.trg_${tn}`, parentFiringId: f.id, statementIndex: 1, observed: rnd() >= 0.15 })
    edge(f.id, tid, 'FIRES', CONF[Math.floor(rnd() * CONF.length)])
    const kids = 1 + Math.floor(rnd() * 3)
    for (let k = 0; k < kids && nodes.length < target; k++) addFiring(tid, 'trigger', f.depth + 1)
  }

  return { graph: { nodes, edges }, firings }
}

const COL_COUNT = 12

export function synthColumns(): string[] {
  return Array.from({ length: COL_COUNT }, (_, i) => `C${String(i + 1).padStart(2, '0')}`)
}

/** 12열짜리 UPDATE 행 n개. 3번째 열마다 바뀐 값, 9번째 열은 예상값, 12번째 열은 파생. */
export function makeGridRows(n: number): GridRow[] {
  const cols = synthColumns()
  const rows: GridRow[] = new Array(n)
  for (let i = 0; i < n; i++) {
    const before: Values = {}
    const after: Values = {}
    const changed = new Set<string>()
    cols.forEach((c, j) => {
      const base = `${c}-${i}`
      before[c] = j === 4 && i % 7 === 0 ? null : base
      after[c] = j % 3 === 2 ? `${base}*` : base
      if (j % 3 === 2) changed.add(c)
    })
    rows[i] = {
      rowId: `r${i}`,
      table: 'Synth.Big',
      op: 'UPDATE',
      multiplicity: null,
      keyText: `Id=${i + 1}`,
      before,
      after,
      changed,
      volatile: new Map<string, VolatileReason>([['C09', 'TRIGGER_GETDATE']]),
      derived: new Set(['C12']),
      firingIds: ['f-1'],
      note: '',
    }
  }
  return rows
}
