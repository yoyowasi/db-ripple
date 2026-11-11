// 간선 모양 규칙(§6.3, C2). 선 모양은 확신도, 색은 종류, 흐린 점선은 관측되지 않은 트리거(observed:false).
// 색만으로 구분하지 않도록 종류는 라벨 글자로도 적는다.
import type { Confidence, GraphEdge, GraphEdgeKind } from '../api/types'

export interface ConfidenceStyle {
  dash: string | undefined
  width: number
  label: string
  description: string
}

export const CONFIDENCE_STYLE: Record<Confidence, ConfidenceStyle> = {
  certain: { dash: undefined, width: 2.6, label: '실선(굵게)', description: '확실: 메타데이터와 키 일치로 확정' },
  inferred: { dash: '9 5', width: 2, label: '파선', description: '추론: 정적 분석·위치 규칙으로 추론' },
  ambiguous: { dash: '12 4 2 4', width: 2, label: '일점쇄선', description: '모호: 후보가 여럿' },
  unexplained: { dash: '3 4', width: 1.8, label: '짧은 점선(노란색)', description: '설명 안 됨: 원인을 못 찾음' },
}

/** 관측되지 않은 트리거로 가는 선: 변경을 남기지 않아 카탈로그로 "발동했을 것"만 추정한 것. */
export const UNOBSERVED_DASH = '1 6'

export const KIND_COLOR: Record<GraphEdgeKind, string> = {
  DIRECT: '#4a86e8',
  FK: '#1aa6a6',
  FIRES: '#8791a0',
  REPLACES: '#a463d8',
  WRITES: '#3fae5f',
  RESIDUAL: '#d9902b',
  BLOCKED: '#dc4b44',
}

export const KIND_LABEL: Record<GraphEdgeKind, string> = {
  DIRECT: '직접',
  FK: 'FK',
  FIRES: '발동',
  REPLACES: '대체(INSTEAD OF)',
  WRITES: '쓰기',
  RESIDUAL: '잔차 후보',
  BLOCKED: '차단',
}

export const KIND_DESCRIPTION: Record<GraphEdgeKind, string> = {
  DIRECT: '문장 → 묶음: 문장의 직접 대상 변경',
  FK: '부모 묶음 → 자식 묶음: FK 참조 동작',
  FIRES: '묶음 → 트리거: 그 묶음이 트리거를 발동시킴',
  REPLACES: '문장 → 트리거: INSTEAD OF가 원래 동작을 대체',
  WRITES: '트리거 → 묶음: 트리거 실행 중 생긴 변경',
  RESIDUAL: '트리거·묶음 → 잔차: 설명되지 않은 최종 변경의 후보 원인',
  BLOCKED: '문장 → 오류: 실행 실패·거부',
}

export const OBSERVED_ONLY_TEXT = '관측 순서, 보장 안 됨'

export function edgeLabelText(e: GraphEdge): string {
  let text = KIND_LABEL[e.kind]
  if (e.kind === 'FK') {
    const parts = [e.fk, e.action, e.matchedRows !== null ? `${e.matchedRows}행` : null].filter(Boolean)
    if (parts.length) text += ` ${parts.join(' · ')}`
  }
  if (e.orderGuarantee === 'observed_only') text += ` · ${OBSERVED_ONLY_TEXT}`
  return text
}
