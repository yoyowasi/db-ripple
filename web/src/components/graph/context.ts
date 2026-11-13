// 전파 그래프 안의 노드·간선 컴포넌트가 함께 읽는 화면 상태. Vue Flow가 노드·간선을 따로 그리므로 provide/inject로 건넨다.
import { inject } from 'vue'
import type { InjectionKey, Ref } from 'vue'
import type { EdgeTone } from '../../lib/graphStyle'

export interface GraphContext {
  /** 강조 중인 경로(마우스·포커스·레일 hover). null이면 강조 없음 */
  active: Readonly<Ref<{ nodes: Set<string>; edges: Set<string> } | null>>
  /** 배율 80% 미만: 의미 기반 축소 표시 */
  compact: Readonly<Ref<boolean>>
  /** 첫 그리기 연출 중 */
  drawing: Readonly<Ref<boolean>>
  /** 첫 그리기 단 사이 지연(초) */
  drawStep: Readonly<Ref<number>>
  selectedFiringId: Readonly<Ref<string | null>>
  showZeroRows: Readonly<Ref<boolean>>
  toggleZeroRows: () => void
  markerId: (tone: EdgeTone) => string
}

export const GRAPH_CONTEXT: InjectionKey<GraphContext> = Symbol('ripple-graph')

export function useGraphContext(): GraphContext {
  const ctx = inject(GRAPH_CONTEXT)
  if (!ctx) throw new Error('PropagationGraph 밖에서 그래프 노드·간선을 그렸습니다.')
  return ctx
}
