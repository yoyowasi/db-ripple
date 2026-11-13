// REST 클라이언트. 모든 경로는 같은 출처의 /api 이고, 개발 중에는 Vite proxy가 API 서버로 넘긴다.
import type {
  AnalysisResult,
  ApplyRequest,
  ApplyResult,
  CatalogSchema,
  ConflictReason,
  NetChange,
  PreviewRequest,
  PreviewResult,
  ProblemDetails,
  ProblemReason,
  RejectItem,
  RowChange,
  RowPage,
  RowView,
  SandboxStatus,
  SettingsRequest,
  SettingsTestResult,
  SettingsView,
} from './types'

const BASE = '/api'

const UNREACHABLE_TEXT = 'API 서버(127.0.0.1:5080)에 연결할 수 없습니다. Host를 실행했는지 확인하세요.'

/** 서버가 요청 출처를 확인하는 헤더. 개발 모드는 고정 값 "1", 실행 파일 모드는 서버가 index.html에 넣은 세션 요청 키(없거나 틀리면 401). */
const CSRF_HEADER = 'X-DbRipple-Request'

/** 실행 파일 모드에서 서버가 index.html에 넣는 요청 키. 없으면(개발 모드) "1". */
function requestKey(): string {
  const meta = typeof document === 'undefined' ? null : document.querySelector('meta[name="dbr-request-key"]')
  return meta?.getAttribute('content') || '1'
}

/** 서버 응답이 아닌 화면 쪽 예외를 ApiError로 감쌀 때 쓰는 상태. 0(네트워크 오류)과 구분해 "서버에 연결할 수 없음"으로 오인하지 않게 한다. */
export const CLIENT_ERROR_STATUS = -1

export class ApiError extends Error {
  readonly status: number
  readonly problem: ProblemDetails | null

  constructor(status: number, problem: ProblemDetails | null, message: string) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.problem = problem
  }

  /** 409·401의 사유(3주차·N4 추가). */
  get reason(): ProblemReason | undefined {
    return this.problem?.reason
  }

  /** 실행 파일이 연 브라우저 창의 세션 쿠키가 없다(401 NO_SESSION). */
  get isNoSession(): boolean {
    return this.status === 401 && this.problem?.reason === 'NO_SESSION'
  }

  /** 422의 거부 목록. */
  get rejects(): RejectItem[] {
    return this.problem?.rejects ?? []
  }

  /** 서버에 닿지 못했다(네트워크 오류, 또는 Vite 프록시가 대상 꺼짐을 ProblemDetails 없는 500·502·504로 바꾼 경우). */
  get isUnreachable(): boolean {
    return this.status === 0 || (this.problem === null && (this.status === 500 || this.status === 502 || this.status === 504))
  }
}

export function isAbortError(e: unknown): boolean {
  return e instanceof DOMException && e.name === 'AbortError'
}

interface RequestOptions {
  body?: unknown
  query?: Record<string, string | number | null | undefined>
  signal?: AbortSignal
}

/**
 * 개발 서버에서 주소에 ?fixture=… 가 있으면 백엔드 대신 dev/fakeApi 의 시험 응답을 돌려준다.
 * import.meta.env.DEV 가 false인 운영 빌드에서는 이 분기와 dev/ 모듈이 통째로 빠진다.
 */
async function devFixtureFetch(method: string, url: string, body: string | undefined, signal?: AbortSignal): Promise<Response | null> {
  if (import.meta.env.DEV) {
    const fake = await import('../dev/fakeApi')
    const fixture = fake.currentFixture()
    if (fixture) return fake.fakeFetch(fixture, method, url, body, signal)
  }
  return null
}

async function request<T>(method: string, path: string, opts: RequestOptions = {}): Promise<T> {
  let url = BASE + path
  if (opts.query) {
    const qs = new URLSearchParams()
    for (const [k, v] of Object.entries(opts.query)) {
      if (v !== undefined && v !== null && v !== '') qs.set(k, String(v))
    }
    const s = qs.toString()
    if (s) url += '?' + s
  }

  const headers: Record<string, string> = {
    Accept: 'application/json, application/problem+json',
    [CSRF_HEADER]: requestKey(),
  }
  let body: string | undefined
  if (opts.body !== undefined) {
    headers['Content-Type'] = 'application/json'
    body = JSON.stringify(opts.body)
  }

  let res: Response
  try {
    res = (await devFixtureFetch(method, url, body, opts.signal)) ?? (await fetch(url, { method, headers, body, signal: opts.signal }))
  } catch (e) {
    if (isAbortError(e)) throw e
    throw new ApiError(0, null, UNREACHABLE_TEXT)
  }

  const text = await res.text()
  let json: unknown = undefined
  if (text) {
    try {
      json = JSON.parse(text)
    } catch {
      json = undefined
    }
  }

  if (!res.ok) {
    // ProblemDetails는 숫자 status를 가진 JSON 객체다. 프록시 오류(빈 본문·텍스트)는 problem 없음으로 둔다.
    const problem =
      json && typeof json === 'object' && typeof (json as ProblemDetails).status === 'number' ? (json as ProblemDetails) : null
    const message = problem?.title ?? problem?.detail ?? `HTTP ${res.status}`
    throw new ApiError(res.status, problem, message)
  }
  return json as T
}

export function getSandbox(signal?: AbortSignal): Promise<SandboxStatus> {
  return request<SandboxStatus>('GET', '/sandbox', { signal })
}

/** 202. 진행은 getSandbox의 state=building으로 본다. */
export async function refreshSandbox(): Promise<void> {
  await request<void>('POST', '/sandbox/refresh')
}

export function getCatalogSchema(signal?: AbortSignal): Promise<CatalogSchema> {
  return request<CatalogSchema>('GET', '/catalog/schema', { signal })
}

export function analyze(sql: string, signal?: AbortSignal): Promise<AnalysisResult> {
  return request<AnalysisResult>('POST', '/analyze', { body: { sql }, signal })
}

export function createPreview(sql: string, imageCap: number | null, signal?: AbortSignal): Promise<PreviewResult> {
  const body: PreviewRequest = imageCap === null ? { sql } : { sql, options: { imageCap } }
  return request<PreviewResult>('POST', '/previews', { body, signal })
}

export function getPreview(id: string, signal?: AbortSignal): Promise<PreviewResult> {
  return request<PreviewResult>('GET', `/previews/${encodeURIComponent(id)}`, { signal })
}

export interface RowQuery {
  view: RowView
  firingId?: string | null
  /** view=net에서만: "스키마.테이블". 서버가 그 테이블만 거른 뒤 페이지를 자르고 total도 거른 수다. */
  table?: string | null
  offset: number
  limit: number
}

export function getRows(
  id: string,
  q: RowQuery,
  signal?: AbortSignal,
): Promise<RowPage<RowChange> | RowPage<NetChange>> {
  return request('GET', `/previews/${encodeURIComponent(id)}/rows`, {
    query: { view: q.view, firingId: q.firingId, table: q.table, offset: q.offset, limit: q.limit },
    signal,
  })
}

/** allowKeysetVerification은 사용자가 약한 검증(KEYSET_HASH)에 동의했을 때만 true로 보낸다(아니면 필드를 빼서 옛 서버와 같은 요청). */
export function applyPreview(id: string, sqlHash: string | null, allowKeysetVerification = false): Promise<ApplyResult> {
  const body: ApplyRequest = allowKeysetVerification ? { sqlHash, allowKeysetVerification: true } : { sqlHash }
  return request<ApplyResult>('POST', `/previews/${encodeURIComponent(id)}/apply`, { body })
}

/** 204. 이미 없으면 404(호출자가 같은 결과로 본다). */
export async function cancelPreview(id: string): Promise<void> {
  await request<void>('POST', `/previews/${encodeURIComponent(id)}/cancel`)
}

// ---- 설정(N4 계약 §4) ----

export function getSettings(signal?: AbortSignal): Promise<SettingsView> {
  return request<SettingsView>('GET', '/settings', { signal })
}

/** 204. 400이면 ApiError.problem.errors 에 필드별 오류가 있다. 환경 변수 설정이면 409 ENV_OVERRIDE. */
export async function saveSettings(body: SettingsRequest): Promise<void> {
  await request<void>('PUT', '/settings', { body })
}

/** 저장하지 않고 연결만 시험한다. */
export function testSettings(body: SettingsRequest, signal?: AbortSignal): Promise<SettingsTestResult> {
  return request<SettingsTestResult>('POST', '/settings/test', { body, signal })
}

export function exportUrl(id: string): string {
  return `${BASE}/previews/${encodeURIComponent(id)}/export`
}

// ---- 오류 문장 ----

const CONFLICT_TEXT: Record<ConflictReason | 'ENV_OVERRIDE', string> = {
  ENV_OVERRIDE: '설정이 환경 변수로 정해져 있어 화면에서 바꿀 수 없습니다.',
  LOCKED: '다른 미리보기나 적용이 진행 중입니다. 끝난 뒤 다시 시도하세요.',
  BUILDING: '기준선을 만드는 중입니다. 상태가 ready가 될 때까지 기다리세요.',
  NO_BASELINE: '기준선이 없습니다. 샌드박스를 새로 고쳐 기준선을 만드세요.',
  FINGERPRINT_MISMATCH: '원본과 샌드박스의 환경 지문이 달라 미리보기할 수 없습니다. 샌드박스를 새로 고치세요.',
  DIRTY: '샌드박스가 오염 상태입니다(롤백 뒤 남은 변경 등). 새로 고침으로 다시 만들어야 합니다.',
}

/** 이 사유일 때 [샌드박스 새로 고침]을 권한다. */
export function conflictNeedsRefresh(reason: ProblemReason | undefined): boolean {
  return reason === 'NO_BASELINE' || reason === 'FINGERPRINT_MISMATCH' || reason === 'DIRTY'
}

/** 사용자에게 보일 한 문단. 400이면 필드별 오류, 409면 사유 문장을 붙인다. */
export function describeApiError(e: ApiError): string {
  if (e.isUnreachable) return UNREACHABLE_TEXT
  const p = e.problem
  switch (e.status) {
    case 400: {
      const lines = Object.entries(p?.errors ?? {}).flatMap(([field, msgs]) => msgs.map((m) => `${field}: ${m}`))
      return lines.length ? `요청이 올바르지 않습니다. ${lines.join(' / ')}` : `요청이 올바르지 않습니다. ${e.message}`
    }
    case 401:
      return '실행 파일이 연 브라우저 창에서만 쓸 수 있습니다. 실행 파일을 다시 여세요.'
    case 403:
      return `요청이 거부되었습니다(403). ${p?.detail ?? p?.title ?? '요청 출처 확인 헤더가 없거나 허용되지 않은 출처입니다.'} 화면을 새로 고친 뒤 다시 시도하세요.`
    case 404:
      return '미리보기를 찾을 수 없습니다. 만료(30분)됐거나 적용·취소로 지워졌습니다.'
    case 409:
      return e.reason && e.reason in CONFLICT_TEXT ? CONFLICT_TEXT[e.reason as keyof typeof CONFLICT_TEXT] : `충돌: ${e.message}`
    case 422:
      return '미리보기할 수 없는 문장이 있습니다. 아래 거부 사유를 확인하세요.'
    case 501:
      return '서버가 아직 지원하지 않는 기능입니다.'
    case 503:
      return '샌드박스 DB에 연결할 수 없습니다(연결 끊김·시간 초과). 잠시 뒤 다시 시도하세요.'
    case CLIENT_ERROR_STATUS:
      return `화면 처리 중 오류가 났습니다: ${e.message}`
    default:
      return `서버 오류(${e.status}): ${p?.detail ?? e.message}`
  }
}
