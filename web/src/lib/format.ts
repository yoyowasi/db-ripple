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

/** 문장 한 줄 표시용 대상: DML은 "스키마.테이블", EXEC는 target이 null이라 "EXEC 스키마.프로시저". */
export function statementTarget(s: { kind: string; target: string | null; procedure?: string | null }): string {
  if (s.kind === 'EXEC') return s.procedure ? `EXEC ${s.procedure}` : 'EXEC'
  return s.target ?? ''
}

/** EXEC 결과 조각: ["반환 값 0", "결과 집합 1"]. 실패·미실행이면 빈 배열(표시할 값이 없다). */
export function execResultParts(s: { returnValue?: number | null; resultSetCount?: number | null }): string[] {
  const parts: string[] = []
  if (s.returnValue !== null && s.returnValue !== undefined) parts.push(`반환 값 ${s.returnValue}`)
  if (s.resultSetCount !== null && s.resultSetCount !== undefined) parts.push(`결과 집합 ${s.resultSetCount}`)
  return parts
}

/** EXEC 결과 한 줄: "반환 값 0 · 결과 집합 1". 표시할 값이 없으면 null. */
export function execResultText(s: { returnValue?: number | null; resultSetCount?: number | null }): string | null {
  const parts = execResultParts(s)
  return parts.length ? parts.join(' · ') : null
}

/** 오류 번호 0은 "프로시저가 트랜잭션을 롤백함"처럼 SQL Server 오류가 아닌 도구 판정이라 번호를 붙이지 않는다. */
export function errorHead(e: { errorNumber: number }): string {
  return e.errorNumber === 0 ? '오류' : `오류 ${e.errorNumber}`
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
  WEAK_VERIFICATION_NOT_ACCEPTED: '원본에 스냅샷 권한이 없어 약한 검증(KEYSET_HASH)이 필요한데 동의하지 않았습니다.',
  PROC_APPLY_NOT_SUPPORTED: '프로시저 호출(EXEC)은 아직 적용할 수 없습니다(미리보기 전용).',
  ORIGIN_UNREACHABLE: '원본에 연결할 수 없습니다. 연결을 복구한 뒤 샌드박스를 새로 고치고 다시 시도하세요.',
}

export function plural(n: number, unit: string): string {
  return `${formatCount(n)}${unit}`
}
