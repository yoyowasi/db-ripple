// 간선·노드 표시 규칙(§6.3, 목업 v6). 선 모양 = 확신도, 색 = 간선 종류(토큰 --k-*), 종류는 라벨 글자로도 적는다.
import type { Confidence, FkAction, GraphEdge, GraphEdgeKind } from '../api/types'
import { CONFIDENCE_LABEL } from './format'

/** 간선 종류 → 색 묶음(CSS 클래스 tone-*). FIRES·WRITES·REPLACES는 트리거 색을 같이 쓴다. */
/** call = EXEC 프로시저 호출 쪽 간선(PROC_DIRECT). v6 토큰 --k-call 을 쓴다(새 색 없음). */
export type EdgeTone = 'direct' | 'call' | 'fk' | 'fires' | 'blocked' | 'residual'
export const EDGE_TONE: Record<GraphEdgeKind, EdgeTone> = {
  DIRECT: 'direct',
  PROC_DIRECT: 'call',
  FK: 'fk',
  FIRES: 'fires',
  REPLACES: 'fires',
  WRITES: 'fires',
  RESIDUAL: 'residual',
  BLOCKED: 'blocked',
}

export const FK_ACTION_TEXT: Record<FkAction, string> = {
  CASCADE: 'CASCADE',
  SET_NULL: 'SET NULL',
  SET_DEFAULT: 'SET DEFAULT',
}

/** 축소 표시(배율 80% 미만)에서 쓰는 짧은 라벨 */
export const KIND_SHORT: Record<GraphEdgeKind, string> = {
  DIRECT: '직접',
  PROC_DIRECT: '프로시저',
  FK: 'FK',
  FIRES: '트리거',
  REPLACES: 'INSTEAD OF',
  WRITES: '쓰기',
  RESIDUAL: '설명 안 됨',
  BLOCKED: '거부',
}

const CONF_SUFFIX: Partial<Record<Confidence, string>> = { inferred: ' · 추론', ambiguous: ' · 모호' }

/** 간선 가운데 알약 글자. 예: "직접", "FK · CASCADE · 추론", "거부" */
export function edgeLabel(e: GraphEdge): string {
  let text = KIND_SHORT[e.kind]
  if (e.kind === 'PROC_DIRECT') text = '프로시저 쓰기'
  if (e.kind === 'FK' && e.action) text = `FK · ${FK_ACTION_TEXT[e.action]}`
  if (e.kind !== 'BLOCKED' && e.kind !== 'RESIDUAL') text += CONF_SUFFIX[e.confidence] ?? ''
  return text
}

export const OBSERVED_ONLY_TEXT = '관측 순서, 보장 안 됨'

const KIND_TITLE: Record<GraphEdgeKind, string> = {
  DIRECT: '문장이 직접 바꾼 테이블',
  PROC_DIRECT: 'EXEC한 프로시저 본문이 직접 쓴 테이블(FK로 설명되지 않는 묶음)',
  FK: '외래 키 연쇄',
  FIRES: '이 묶음이 트리거를 발동시켰다고 보는 연결',
  REPLACES: 'INSTEAD OF 트리거가 원래 동작을 대신함',
  WRITES: '트리거 실행 중 생긴 변경',
  RESIDUAL: '설명되지 않은 변경의 후보 원인',
  BLOCKED: '문장이 실패하거나 트리거가 거부함',
}

/** 간선 tooltip: FK 전체 이름·action·확신도·일치 행 수 */
export function edgeTitle(e: GraphEdge): string {
  const parts: string[] = []
  if (e.kind === 'FK') {
    if (e.fk) parts.push(e.fk)
    parts.push(e.action ? FK_ACTION_TEXT[e.action] : '외래 키')
  } else {
    parts.push(KIND_TITLE[e.kind])
  }
  parts.push(`확신도 ${CONFIDENCE_LABEL[e.confidence]}`)
  if (e.matchedRows !== null) parts.push(`일치 ${e.matchedRows.toLocaleString('en-US')}행`)
  if (e.orderGuarantee === 'observed_only') parts.push(`형제 순서 ${OBSERVED_ONLY_TEXT}`)
  return parts.join(' · ')
}

/** 범례 "선 모양" 글자 */
export const CONFIDENCE_LINE: Record<Confidence, string> = {
  certain: '실선 확실',
  inferred: '파선 추론',
  ambiguous: '점선 모호',
  unexplained: '점선 설명 안 됨',
}

/** 범례 "읽는 법"의 간선 줄 */
export const TONE_LEGEND: Record<EdgeTone, [string, string]> = {
  direct: ['직접', '문장이 직접 바꾼 테이블'],
  call: ['프로시저 쓰기', 'EXEC한 프로시저 본문이 직접 쓴 테이블'],
  fk: ['FK · CASCADE', '외래 키 연쇄. 전체 FK 이름은 간선에 마우스를 올리면 보입니다'],
  fires: ['트리거 · 쓰기', '트리거 발동과 그 쓰기(INSTEAD OF 포함)'],
  blocked: ['거부', '문장이 실패하거나 트리거가 거부함'],
  residual: ['설명 안 됨', '설명되지 않은 변경의 후보 원인'],
}
