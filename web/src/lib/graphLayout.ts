// dagre 배치. 인과 순서(문장 → 묶음 → 트리거 → 묶음 …)가 왼쪽에서 오른쪽으로 흐르게 한다. 노드 크기는 노드마다 다르다.
import * as dagreNs from '@dagrejs/dagre'

// @dagrejs/dagre는 버전에 따라 default 또는 이름 있는 내보내기로 노출된다. 둘 다 받는다.
// eslint-disable-next-line @typescript-eslint/no-explicit-any
const dagre: any = (dagreNs as any).default ?? dagreNs

export interface LayoutInput {
  /** 넣는 순서가 같은 단의 첫 순서가 된다(표시 순번 순으로 넣는다). */
  nodes: { id: string; width: number; height: number }[]
  edges: { source: string; target: string }[]
  /** 단 사이 간격. 간선 가운데 라벨 알약이 들어갈 만큼 둔다. */
  ranksep: number
}

export interface LayoutResult {
  positions: Map<string, { x: number; y: number }>
  ms: number
}

export function layoutGraph(input: LayoutInput): LayoutResult {
  const t0 = performance.now()
  const g = new dagre.graphlib.Graph()
  g.setGraph({ rankdir: 'LR', nodesep: 16, ranksep: input.ranksep, marginx: 16, marginy: 16 })
  g.setDefaultEdgeLabel(() => ({}))

  const ids = new Set<string>()
  for (const n of input.nodes) {
    g.setNode(n.id, { width: n.width, height: n.height })
    ids.add(n.id)
  }
  for (const e of input.edges) {
    if (ids.has(e.source) && ids.has(e.target)) g.setEdge(e.source, e.target)
  }
  dagre.layout(g)

  const positions = new Map<string, { x: number; y: number }>()
  for (const n of input.nodes) {
    const p = g.node(n.id) as { x: number; y: number } | undefined
    // dagre는 중심 좌표를 준다. Vue Flow는 왼쪽 위 좌표를 쓴다.
    positions.set(n.id, p ? { x: p.x - n.width / 2, y: p.y - n.height / 2 } : { x: 0, y: 0 })
  }
  return { positions, ms: performance.now() - t0 }
}

// ---- 글자 폭 어림(목업 v6과 같은 규칙). 노드 폭을 배치 전에 정하려고 쓴다. ----

/** UI 글꼴 폭: 한글 등 넓은 글자 12.4px, 나머지 7px(12px 기준) */
export function textW(s: string, size = 12, bold = false): number {
  let w = 0
  for (let i = 0; i < s.length; i++) w += s.charCodeAt(i) > 255 ? 12.4 : 7
  return ((w * size) / 12) * (bold ? 1.06 : 1)
}

/** 고정폭 글꼴 폭(0.6em) */
export function monoW(s: string, size = 12): number {
  return s.length * size * 0.62
}
