// PreviewResult.graph → Vue Flow 노드·간선. 플래그는 DTO 필드에서 계산한다(§6.3, Q15):
//   zeroRows = rowCount == 0, partial = imageMode != FULL, auditNotLast = auditOrder == NONE
import { MarkerType } from '@vue-flow/core'
import type { Edge, Node } from '@vue-flow/core'
import type { Firing, GraphDto, GraphEdge, GraphNode } from '../api/types'
import { layoutGraph } from './graphLayout'
import { KIND_COLOR, edgeLabelText } from './graphStyle'

export interface NodeFlags {
  zeroRows: boolean
  partial: boolean
  auditNotLast: boolean
}

export interface RippleNodeData {
  node: GraphNode
  firing: Firing | null
  flags: NodeFlags
}

export interface RippleEdgeData {
  edge: GraphEdge
  labelText: string
  /** 목표가 observed:false 트리거(변경을 남기지 않은 추정 발동)인 간선. */
  toUnobserved: boolean
}

export interface FlowModel {
  nodes: Node<RippleNodeData>[]
  edges: Edge<RippleEdgeData>[]
  hiddenZeroRows: number
  totalNodes: number
  layoutMs: number
}

export function buildFlow(graph: GraphDto, firings: Firing[], showZeroRows: boolean): FlowModel {
  const firingById = new Map(firings.map((f) => [f.id, f]))
  const byId = new Map(graph.nodes.map((n) => [n.id, n]))

  // E23: 0행 묶음은 기본으로 숨긴다. 문장 노드의 rowsAffected=0 표시는 그대로 둔다.
  const hidden = new Set<string>()
  for (const n of graph.nodes) {
    if (n.type === 'firing' && n.rowCount === 0 && !showZeroRows) hidden.add(n.id)
  }

  const visible = graph.nodes.filter((n) => !hidden.has(n.id))
  const visibleEdges = graph.edges.filter((e) => !hidden.has(e.source) && !hidden.has(e.target))
  const layout = layoutGraph({ nodes: visible, edges: visibleEdges })

  const nodes: Node<RippleNodeData>[] = visible.map((n) => {
    const firing = n.type === 'firing' ? (firingById.get(n.firingId) ?? null) : null
    const flags: NodeFlags = {
      zeroRows: n.type === 'firing' && n.rowCount === 0,
      partial: firing !== null && firing.imageMode !== 'FULL',
      auditNotLast: firing !== null && firing.auditOrder === 'NONE',
    }
    return {
      id: n.id,
      type: 'ripple',
      position: layout.positions.get(n.id) ?? { x: 0, y: 0 },
      data: { node: n, firing, flags },
      draggable: false,
      connectable: false,
    }
  })

  const edges: Edge<RippleEdgeData>[] = visibleEdges.map((e) => {
    const target = byId.get(e.target)
    return {
      id: e.id,
      source: e.source,
      target: e.target,
      type: 'ripple',
      data: {
        edge: e,
        labelText: edgeLabelText(e),
        toUnobserved: target?.type === 'trigger' && !target.observed,
      },
      markerEnd: { type: MarkerType.ArrowClosed, color: KIND_COLOR[e.kind] },
    }
  })

  return { nodes, edges, hiddenZeroRows: hidden.size, totalNodes: graph.nodes.length, layoutMs: layout.ms }
}

/** 노드 하나를 한 줄로 설명한다(상세 패널·aria-label). */
export function describeNode(n: GraphNode): string {
  switch (n.type) {
    case 'statement':
      return `문장 #${n.statementIndex}: ${n.label}`
    case 'firing':
      return `변경 묶음 ${n.table} ${n.operation} ${n.rowCount}행(깊이 ${n.depth}, 순번 ${n.captureSeq})`
    case 'trigger':
      return `트리거 ${n.trigger}${n.observed ? '' : '(변경을 남기지 않음, 카탈로그 기준 추정)'}`
    case 'residual':
      return `잔차 ${n.table}(${n.category})`
    case 'error':
      return `오류 ${n.errorNumber}: ${n.message}`
    case 'group':
      return `접힌 묶음 ${n.members.length}개(${n.rowCount}행)`
  }
}
