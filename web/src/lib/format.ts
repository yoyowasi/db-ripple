// 표시용 문자열 도우미(순수 함수).
import type {
  ApplyPrecondition,
  ApplyStatus,
  Confidence,
  NetChangeKind,
  Op,
  PreviewStatus,
  ResidualCategory,
  RowKey,
  SourceSpan,
  StatementStatus,
  VolatileReason,
} from '../api/types'

export function formatTime(iso: string | null | undefined): string {
  if (!iso) return '-'
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return iso
  return d.toLocaleString('ko-KR', { hour12: false })
}

export function formatMs(ms: number | null | undefined): string {
  if (ms === null || ms === undefined) return '-'
  if (ms >= 1000) return `${(ms / 1000).toFixed(2)}초`
  return `${Math.round(ms)}ms`
}

export function formatCount(n: number): string {
  return n.toLocaleString('ko-KR')
}

export function shortHash(h: string): string {
  return h.length > 18 ? `${h.slice(0, 18)}…` : h
}

export function spanText(s: SourceSpan): string {
  return `${s.startLine}행 ${s.startColumn}열`
}

export function keyText(k: RowKey): string {
  if (k.kind === 'ROW') return '(키 없음)'
  const pairs = k.columns.map((c, i) => `${c}=${k.values[i] ?? 'NULL'}`)
  return pairs.join(', ')
}

export function mmss(ms: number): string {
  const total = Math.max(0, Math.floor(ms / 1000))
  const m = Math.floor(total / 60)
  const s = total % 60
  return `${m}:${String(s).padStart(2, '0')}`
}

export const OP_LABEL: Record<Op, string> = { INSERT: '추가', UPDATE: '수정', DELETE: '삭제' }

export const NET_CHANGE_LABEL: Record<NetChangeKind, string> = {
  INSERTED: '추가',
  UPDATED: '수정',
  DELETED: '삭제',
  MULTISET_REMOVED: '제거(다중집합)',
  MULTISET_ADDED: '추가(다중집합)',
}

export const CONFIDENCE_LABEL: Record<Confidence, string> = {
  certain: '확실',
  inferred: '추론',
  ambiguous: '모호(후보 여럿)',
  unexplained: '설명 안 됨',
}

export const PREVIEW_STATUS_LABEL: Record<PreviewStatus, string> = {
  ok: '정상',
  failed: '실패',
  trigger_blocked: '트리거가 거부함',
  truncated: '상한 초과(잘림)',
}

export const STATEMENT_STATUS_LABEL: Record<StatementStatus, string> = {
  ok: '성공',
  failed: '실패',
  trigger_blocked: '트리거 거부',
  not_run: '실행 안 함',
}

export const RESIDUAL_CATEGORY_LABEL: Record<ResidualCategory, string> = {
  EXCLUDED_TABLE: '수집 제외 테이블',
  UNOBSERVABLE_COLUMN: '관측 불가 열',
  SAME_TABLE_TRIGGER: '같은 테이블 트리거',
  UNKNOWN: '원인 미상',
}

export const VOLATILE_LABEL: Record<VolatileReason, string> = {
  IDENTITY: '새 IDENTITY 값',
  SEQUENCE_DEFAULT: 'SEQUENCE 기본값',
  NONDETERMINISTIC_DEFAULT: '비결정 기본값(GETDATE·NEWID 등)',
  STATEMENT_FUNCTION: '문장 안 비결정 함수',
  TRIGGER_GETDATE: '트리거 안 GETDATE()',
  ROWVERSION: 'rowversion',
}

export const APPLY_STATUS_LABEL: Record<ApplyStatus, string> = {
  committed: '원본에 적용됨(COMMIT)',
  rolled_back_mismatch: '롤백됨: 미리보기와 다른 결과',
  drift_detected: '적용 안 함: 미리보기 뒤 원본이 바뀜(드리프트)',
  precondition_failed: '적용 안 함: 선행 조건 실패',
  error: '오류로 롤백됨',
}

export const PRECONDITION_LABEL: Record<ApplyPrecondition, string> = {
  PREVIEW_NOT_OK: '미리보기 상태가 정상이 아닙니다(실패·거부·상한 초과 포함).',
  SQL_HASH_MISMATCH: '미리보기 SQL과 적용 요청의 SQL 해시가 다릅니다.',
  PREVIEW_EXPIRED: '미리보기가 만료됐습니다(30분).',
  FINGERPRINT_MISMATCH: '원본과 환경 지문이 다릅니다.',
  EXECUTE_AS_USER_MISSING: '원본 DB에 실행 사용자(dbr_preview_user)가 없습니다. 준비 단계가 필요합니다.',
}

export function plural(n: number, unit: string): string {
  return `${formatCount(n)}${unit}`
}
