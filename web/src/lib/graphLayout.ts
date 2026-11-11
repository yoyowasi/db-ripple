// dagre 배치. 인과 순서(문장 → 묶음 → 트리거 → 묶음 …)가 왼쪽에서 오른쪽으로 흐르게 한다.
import * as dagreNs from '@dagrejs/dagre'

// @dagrejs/dagre는 버전에 따라 default 또는 이름 있는 내보내기로 노출된다. 둘 다 받는다.
// eslint-disable-next-line @typescript-eslint/no-explicit-any
const dagre: any = (dagreNs as any).default ?? dagreNs

export const NODE_W = 248
export const NODE_H = 88

export interface LayoutInput {
  nodes: { id: string }[]
  edges: { source: string; target: string }[]
}

export interface LayoutResult {
  positions: Map<string, { x: number; y: number }>
  ms: number
}

export function layoutGraph(input: LayoutInput): LayoutResult {
  const t0 = performance.now()
  const g = new dagre.graphlib.Graph()
  g.setGraph({ rankdir: 'LR', nodesep: 22, ranksep: 96, marginx: 16, marginy: 16 })
  g.setDefaultEdgeLabel(() => ({}))

  const ids = new Set<string>()
  for (const n of input.nodes) {
    g.setNode(n.id, { width: NODE_W, height: NODE_H })
    ids.add(n.id)
  }
  for (const e of input.edges) {
    // 숨긴 노드로 가는 간선은 이미 걸러졌지만, 방어적으로 한 번 더 거른다.
    if (ids.has(e.source) && ids.has(e.target)) g.setEdge(e.source, e.target)
  }
  dagre.layout(g)

  const positions = new Map<string, { x: number; y: number }>()
  for (const n of input.nodes) {
    const p = g.node(n.id) as { x: number; y: number } | undefined
    // dagre는 중심 좌표를 준다. Vue Flow는 왼쪽 위 좌표를 쓴다.
    positions.set(n.id, p ? { x: p.x - NODE_W / 2, y: p.y - NODE_H / 2 } : { x: 0, y: 0 })
  }
  return { positions, ms: performance.now() - t0 }
}
