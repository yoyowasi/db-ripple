// 개발 전용 가짜 API. api/client.ts 가 ?fixture=… 일 때 fetch 대신 부른다. 응답은 실제 서버처럼 Response 로 돌려준다
// (같은 상태 코드·ProblemDetails 처리 경로를 그대로 탄다). 운영 빌드에는 들어가지 않는다.
import type { ApplyRequest, ProblemDetails, RowView, SandboxStatus, SettingsRequest, SettingsTestResult, SettingsView } from '../api/types'
import { FIXTURE_IDS, FIXTURES } from './fixtures'
import type { Fixture, FixtureId, RowSource } from './fixtures'

/** 주소의 ?fixture= 값(목록에 있을 때만). */
export function currentFixture(): FixtureId | null {
  const v = new URLSearchParams(window.location.search).get('fixture')
  return v && (FIXTURE_IDS as readonly string[]).includes(v) ? (v as FixtureId) : null
}

// 페이지를 새로 열면 처음으로 돌아가는 상태(새로 고침·적용 흉내)
const state = {
  buildingUntil: 0,
  baselineAt: '',
  previews: new Map<string, { createdAt: string; consumed: boolean }>(),
  settings: null as SettingsView | null,
}

const DEFAULT_SETTINGS: SettingsView = {
  source: 'profile',
  configured: true,
  mode: 'split',
  origin: { server: 'devsql01', database: 'AW_Origin', auth: 'windows', user: null, hasStoredPassword: false, trustServerCertificate: false },
  sandbox: { server: '127.0.0.1,14333', database: 'AW_Sandbox', auth: 'sql', user: 'sa', hasStoredPassword: true },
  backup: { serverPath: null, sharePath: null, importFile: null },
}

const LOOPBACK = /^(127\.0\.0\.1|localhost|::1|\(local\)|\.)(,\d+)?$/i

/** 저장·시험 요청 검사. 시험 데이터라 몇 가지 대표 오류만 흉내 낸다(실제 문구는 백엔드가 정한다). */
function validateSettings(req: SettingsRequest, stored: SettingsView): Record<string, string[]> {
  const e: Record<string, string[]> = {}
  const add = (k: string, m: string) => (e[k] = [...(e[k] ?? []), m])
  const split = req.mode === 'split'
  if (split && !req.origin.server.trim()) add('origin.server', '원본 서버를 입력하세요.')
  if (!req.origin.database.trim()) add('origin.database', '원본 DB 이름을 입력하세요.')
  if (!req.sandbox.server.trim()) add('sandbox.server', '샌드박스 서버를 입력하세요.')
  else if (split && !LOOPBACK.test(req.sandbox.server.trim())) add('sandbox.server', '샌드박스는 이 PC의 SQL Server만 쓸 수 있습니다')
  if (!req.sandbox.database.trim()) add('sandbox.database', '샌드박스 DB 이름을 입력하세요.')
  else if (!split && req.sandbox.database.trim().toLowerCase() === req.origin.database.trim().toLowerCase())
    add('sandbox.database', '한 서버 모드에서는 원본 DB와 이름이 달라야 합니다.')
  if (split && req.origin.auth === 'sql') {
    if (!req.origin.user) add('origin.user', '사용자를 입력하세요.')
    if (!req.origin.password && !stored.origin?.hasStoredPassword) add('origin.password', '비밀번호를 입력하세요.')
  }
  if (req.sandbox.auth === 'sql' && !req.sandbox.password && !stored.sandbox?.hasStoredPassword) add('sandbox.password', '비밀번호를 입력하세요.')
  return e
}

function testResultOf(req: SettingsRequest): SettingsTestResult {
  const bad = /bad/i.test(req.origin.server)
  const nosnap = /nosnap/i.test(req.origin.server)
  const sysadmin = req.origin.user === 'sa'
  const warnings: string[] = []
  if (sysadmin) warnings.push('원본 로그인이 sysadmin입니다. [적용]은 확인을 한 번 더 받습니다.')
  if (nosnap) warnings.push('원본에 스냅샷(CREATE DATABASE) 권한이 없습니다. [적용]이 약한 검증(KEYSET)으로 동의를 묻습니다.')
  return {
    origin:
      req.mode === 'split'
        ? bad
          ? { ok: false, error: '연결 실패(오류 18456)' }
          : { ok: true, serverName: req.origin.server.toUpperCase(), productVersion: '16.0.4175.1', edition: 'Developer', isSysadmin: sysadmin, canBackup: true, canCreateDatabase: !nosnap, error: null }
        : null,
    sandbox: { ok: true, serverName: 'DESKTOP-DBR', productVersion: '17.0.1000.7', isLoopback: LOOPBACK.test(req.sandbox.server.trim()), error: null },
    warnings,
  }
}

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })
}

function problem(p: ProblemDetails): Response {
  return new Response(JSON.stringify(p), { status: p.status, headers: { 'Content-Type': 'application/problem+json' } })
}

const notFound = () => problem({ status: 404, title: '미리보기를 찾을 수 없습니다.' })
const buildingConflict = () =>
  problem({ type: 'https://example.local/problems/conflict', status: 409, title: '기준선을 만드는 중입니다.', reason: 'BUILDING' })

function delay(ms: number, signal?: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    const abort = () => reject(new DOMException('요청을 끊었습니다.', 'AbortError'))
    if (signal?.aborted) return abort()
    const t = window.setTimeout(resolve, ms)
    signal?.addEventListener(
      'abort',
      () => {
        window.clearTimeout(t)
        abort()
      },
      { once: true },
    )
  })
}

function sandboxOf(fx: Fixture): SandboxStatus {
  if (!state.baselineAt) state.baselineAt = fx.sandbox.baselineAt ?? ''
  if (fx.sandbox.state !== 'ready') return fx.sandbox
  if (Date.now() < state.buildingUntil) return { ...fx.sandbox, state: 'building', baselineAt: null }
  return { ...fx.sandbox, baselineAt: state.baselineAt }
}

function startRebuild(ms: number): void {
  state.buildingUntil = Date.now() + ms
  // 새 기준선 시각 = 다 만든 시각(스키마 캐시 키가 바뀐다).
  state.baselineAt = new Date(state.buildingUntil).toISOString()
}

function page<T>(src: RowSource<T>, firingId: string | null, match: (row: T, id: string) => boolean, offset: number, limit: number) {
  if (Array.isArray(src)) {
    const rows = firingId ? src.filter((r) => match(r, firingId)) : src
    return { total: rows.length, rows: rows.slice(offset, offset + limit) }
  }
  if (firingId && !src.firingIds.includes(firingId)) return { total: 0, rows: [] as T[] }
  const end = Math.min(src.total, offset + limit)
  const rows: T[] = []
  for (let i = offset; i < end; i++) rows.push(src.make(i))
  return { total: src.total, rows }
}

/** 시험 데이터로 응답을 만든다. 경로·메서드·상태 코드는 docs/architecture.md §6.4 와 api/client.ts 를 따른다. */
export async function fakeFetch(id: FixtureId, method: string, url: string, body: string | undefined, signal?: AbortSignal): Promise<Response> {
  const fx = FIXTURES[id]
  const u = new URL(url, window.location.origin)
  const path = u.pathname.replace(/^\/api/, '')

  // ---- 설정 API(N4 §4) ----
  if (path === '/settings' || path === '/settings/test') {
    if (id === 'nosession') return problem(fx.sandboxProblem!)
    state.settings ??= fx.settings ?? DEFAULT_SETTINGS
    const cur = state.settings
    await delay(120, signal)
    if (method === 'GET') return json(200, cur)
    const req = JSON.parse(body ?? '{}') as SettingsRequest
    if (method === 'PUT') {
      if (cur.source === 'env') return problem({ status: 409, title: '설정이 환경 변수로 정해져 있습니다.', reason: 'ENV_OVERRIDE' })
      const errors = validateSettings(req, cur)
      if (Object.keys(errors).length) return problem({ status: 400, title: '요청이 올바르지 않습니다.', errors })
      state.settings = {
        source: 'profile',
        configured: true,
        mode: req.mode,
        origin: {
          server: req.origin.server,
          database: req.origin.database,
          auth: req.origin.auth,
          user: req.origin.user,
          hasStoredPassword: req.origin.auth === 'sql' && (req.origin.rememberPassword ? !!req.origin.password || !!cur.origin?.hasStoredPassword : false),
          trustServerCertificate: req.origin.trustServerCertificate,
        },
        sandbox: {
          server: req.sandbox.server,
          database: req.sandbox.database,
          auth: req.sandbox.auth,
          user: req.sandbox.user,
          hasStoredPassword: req.sandbox.auth === 'sql' && (req.sandbox.rememberPassword ? !!req.sandbox.password || !!cur.sandbox?.hasStoredPassword : false),
        },
        backup: req.backup,
      }
      return new Response(null, { status: 204 })
    }
    if (method === 'POST') {
      const errors = validateSettings(req, cur)
      if (Object.keys(errors).length) return problem({ status: 400, title: '요청이 올바르지 않습니다.', errors })
      return json(200, testResultOf(req))
    }
  }
  if (fx.sandboxProblem) return problem(fx.sandboxProblem)
  const sb = sandboxOf(fx)
  const building = sb.state === 'building'
  const route = `${method} ${path}`

  if (route === 'GET /sandbox') {
    await delay(60, signal)
    return json(200, sb)
  }
  if (route === 'POST /sandbox/refresh') {
    await delay(60, signal)
    if (fx.sandbox.state === 'ready') startRebuild(4000)
    return new Response(null, { status: 202, headers: { Location: '/api/sandbox' } })
  }
  if (route === 'GET /catalog/schema') {
    await delay(120, signal)
    if (building || !fx.schema || !sb.baselineAt) return buildingConflict()
    return json(200, { ...fx.schema, baselineAt: sb.baselineAt })
  }
  if (route === 'POST /analyze') {
    await delay(150, signal)
    const sql = (JSON.parse(body ?? '{}') as { sql?: string }).sql ?? ''
    if (!sql.trim()) return problem({ status: 400, title: '요청이 올바르지 않습니다.', errors: { sql: ['SQL이 비어 있습니다.'] } })
    // 입력한 SQL과 관계없이 이 시험 데이터의 분석 결과를 돌려준다.
    return json(200, fx.analysis)
  }
  if (route === 'POST /previews') {
    await delay(700, signal)
    if (building && typeof fx.preview === 'function') return buildingConflict()
    if (typeof fx.preview !== 'function') return problem(fx.preview)
    const createdAt = new Date().toISOString()
    const p = fx.preview(createdAt)
    state.previews.set(p.previewId, { createdAt, consumed: false })
    return json(200, p)
  }

  const m = /^\/previews\/([^/]+)(?:\/(rows|apply|cancel|export))?$/.exec(path)
  if (!m || typeof fx.preview !== 'function') return notFound()
  const previewId = decodeURIComponent(m[1])
  const known = state.previews.get(previewId)
  if (!known || known.consumed) return notFound()
  const action = m[2] ?? ''

  if (method === 'GET' && action === '') {
    await delay(80, signal)
    return json(200, fx.preview(known.createdAt))
  }
  if (method === 'GET' && action === 'rows') {
    await delay(120, signal)
    const view = (u.searchParams.get('view') ?? 'event') as RowView
    const offset = Number(u.searchParams.get('offset') ?? 0)
    const limit = Number(u.searchParams.get('limit') ?? 100)
    const firingId = u.searchParams.get('firingId')
    if ((view !== 'event' && view !== 'net') || !(offset >= 0) || !(limit >= 1 && limit <= 1000)) {
      return problem({ status: 400, title: '요청이 올바르지 않습니다.', errors: { query: ['view=event|net, offset>=0, 1<=limit<=1000'] } })
    }
    const res =
      view === 'event'
        ? page(fx.eventRows, firingId, (r, f) => r.firingId === f, offset, limit)
        : page(fx.netRows, firingId, (r, f) => r.explainedBy?.firingIds.includes(f) ?? false, offset, limit)
    return json(200, { total: res.total, view, offset, limit, rows: res.rows })
  }
  if (method === 'POST' && action === 'apply') {
    await delay(600, signal)
    if (!fx.apply) return notFound()
    const req = JSON.parse(body ?? '{}') as ApplyRequest
    const r = fx.apply(new Date().toISOString(), previewId, req)
    if (r.status === 'committed') {
      known.consumed = true
      startRebuild(8000)
    }
    return json(200, r)
  }
  if (method === 'POST' && action === 'cancel') {
    await delay(60, signal)
    known.consumed = true
    return new Response(null, { status: 204 })
  }
  return notFound()
}
