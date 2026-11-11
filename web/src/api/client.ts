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
  RejectItem,
  RowChange,
  RowPage,
  RowView,
  SandboxStatus,
} from './types'

const BASE = '/api'

export class ApiError extends Error {
  readonly status: number
  readonly problem: ProblemDetails | null

  constructor(status: number, problem: ProblemDetails | null, message: string) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.problem = problem
  }

  /** 409의 사유(3주차 추가). */
  get reason(): ConflictReason | undefined {
    return this.problem?.reason
  }

  /** 422의 거부 목록. */
  get rejects(): RejectItem[] {
    return this.problem?.rejects ?? []
  }

  /** 서버에 닿지 못했다(네트워크, 프록시 대상 꺼짐). */
  get isUnreachable(): boolean {
    return this.status === 0
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

  const headers: Record<string, string> = { Accept: 'application/json, application/problem+json' }
  let body: string | undefined
  if (opts.body !== undefined) {
    headers['Content-Type'] = 'application/json'
    body = JSON.stringify(opts.body)
  }

  let res: Response
  try {
    res = await fetch(url, { method, headers, body, signal: opts.signal })
  } catch (e) {
    if (isAbortError(e)) throw e
    throw new ApiError(0, null, '서버에 연결할 수 없습니다. API 서버(127.0.0.1:5080)가 켜져 있는지 확인하세요.')
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
    const problem = (json && typeof json === 'object' ? (json as ProblemDetails) : null) ?? null
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
  offset: number
  limit: number
}

export function getRows(
  id: string,
  q: RowQuery,
  signal?: AbortSignal,
): Promise<RowPage<RowChange> | RowPage<NetChange>> {
  return request('GET', `/previews/${encodeURIComponent(id)}/rows`, {
    query: { view: q.view, firingId: q.firingId, offset: q.offset, limit: q.limit },
    signal,
  })
}

export function applyPreview(id: string, sqlHash: string | null): Promise<ApplyResult> {
  const body: ApplyRequest = { sqlHash }
  return request<ApplyResult>('POST', `/previews/${encodeURIComponent(id)}/apply`, { body })
}

/** 204. 이미 없으면 404(호출자가 같은 결과로 본다). */
export async function cancelPreview(id: string): Promise<void> {
  await request<void>('POST', `/previews/${encodeURIComponent(id)}/cancel`)
}

export function exportUrl(id: string): string {
  return `${BASE}/previews/${encodeURIComponent(id)}/export`
}

// ---- 오류 문장 ----

const CONFLICT_TEXT: Record<ConflictReason, string> = {
  LOCKED: '다른 미리보기나 적용이 진행 중입니다. 끝난 뒤 다시 시도하세요.',
  BUILDING: '기준선을 만드는 중입니다. 상태가 ready가 될 때까지 기다리세요.',
  NO_BASELINE: '기준선이 없습니다. 샌드박스를 새로 고쳐 기준선을 만드세요.',
  FINGERPRINT_MISMATCH: '원본과 샌드박스의 환경 지문이 달라 미리보기할 수 없습니다. 샌드박스를 새로 고치세요.',
  DIRTY: '샌드박스가 오염 상태입니다(롤백 뒤 남은 변경 등). 새로 고침으로 다시 만들어야 합니다.',
}

/** 이 사유일 때 [샌드박스 새로 고침]을 권한다. */
export function conflictNeedsRefresh(reason: ConflictReason | undefined): boolean {
  return reason === 'NO_BASELINE' || reason === 'FINGERPRINT_MISMATCH' || reason === 'DIRTY'
}

/** 사용자에게 보일 한 문단. 400이면 필드별 오류, 409면 사유 문장을 붙인다. */
export function describeApiError(e: ApiError): string {
  if (e.isUnreachable) return e.message
  const p = e.problem
  switch (e.status) {
    case 400: {
      const lines = Object.entries(p?.errors ?? {}).flatMap(([field, msgs]) => msgs.map((m) => `${field}: ${m}`))
      return lines.length ? `요청이 올바르지 않습니다. ${lines.join(' / ')}` : `요청이 올바르지 않습니다. ${e.message}`
    }
    case 404:
      return '미리보기를 찾을 수 없습니다. 만료(30분)됐거나 적용·취소로 지워졌습니다.'
    case 409:
      return e.reason ? CONFLICT_TEXT[e.reason] : `충돌: ${e.message}`
    case 422:
      return '미리보기할 수 없는 문장이 있습니다. 아래 거부 사유를 확인하세요.'
    case 501:
      return '서버가 아직 지원하지 않는 기능입니다.'
    case 503:
      return '샌드박스 DB에 연결할 수 없습니다(연결 끊김·시간 초과). 잠시 뒤 다시 시도하세요.'
    default:
      return `서버 오류(${e.status}): ${p?.detail ?? e.message}`
  }
}
