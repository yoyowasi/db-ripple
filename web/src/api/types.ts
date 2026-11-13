// docs/week3-plan.md §5(API JSON 모양)를 손으로 옮긴 타입이다. 유일한 기준은 §5이고,
// 테스트 라운드에서 openapi-typescript 생성 타입과 대조한다(Q41, 위험 7).
// ContractJson 기준: camelCase, 열거형은 설계 문자열, null 속성도 포함한다.

export type Iso = string // DateTimeOffset ISO 8601
export type Values = Record<string, string | null> // §6.1: 값은 모두 문자열, NULL은 null
export type KeyKind = 'PK' | 'UNIQUE' | 'ROW'
export type Op = 'INSERT' | 'UPDATE' | 'DELETE'
/** 문장 종류. 행 연산(Op)에 저장 프로시저 호출(EXEC)이 더해진다. Firing·RowChange에는 EXEC가 나오지 않는다. */
export type StatementKind = Op | 'EXEC'
export type Confidence = 'certain' | 'inferred' | 'ambiguous' | 'unexplained'
export type OrderGuarantee = 'guaranteed' | 'observed_only'
export type Evidence =
  | 'depth'
  | 'postorderBracket'
  | 'staticWriteSet'
  | 'fkKeyMatch'
  | 'catalogInsteadOf'
  | 'reread'
  | 'netDiff'
export type VolatileReason =
  | 'IDENTITY'
  | 'SEQUENCE_DEFAULT'
  | 'NONDETERMINISTIC_DEFAULT'
  | 'STATEMENT_FUNCTION'
  | 'TRIGGER_GETDATE'
  | 'ROWVERSION'

export interface SourceSpan {
  startLine: number
  startColumn: number
  endLine: number
  endColumn: number
}
export interface RowKey {
  kind: KeyKind
  columns: string[]
  values: (string | null)[]
}

export type ConflictReason = 'LOCKED' | 'BUILDING' | 'NO_BASELINE' | 'FINGERPRINT_MISMATCH' | 'DIRTY'

export interface RejectItem {
  statementIndex: number | null
  ruleId: string
  span: SourceSpan
}

/** 401 NO_SESSION · 409 ENV_OVERRIDE(설정 API, 환경 변수가 우선일 때) */
export type ProblemReason = ConflictReason | 'ENV_OVERRIDE' | 'NO_SESSION'

/** 400·401·404·409·422·500·501·503 */
export interface ProblemDetails {
  type?: string
  title?: string
  status: number
  detail?: string
  instance?: string
  traceId?: string
  /** 400 ValidationProblem (예 errors.sql, errors["options.imageCap"]) */
  errors?: Record<string, string[]>
  /** 422, type = "https://example.local/problems/rejected" */
  rejects?: RejectItem[]
  /** 409 (3주차 추가) · 401 NO_SESSION · 409 ENV_OVERRIDE(N4 계약) */
  reason?: ProblemReason
}

// GET /api/sandbox → 200
export type SandboxState = 'building' | 'ready' | 'dirty' | 'error'
export type ConnectionMode = 'split' | 'single_server'
/** N4 계약 §5. 아래 새 필드는 모두 선택이다(옛 서버 응답에도 화면이 동작한다). */
export interface SandboxOrigin {
  server: string
  database: string
  /** SQL 인증이면 사용자 이름, Windows 인증이면 "Windows 인증". 비밀값 없음. */
  login?: string
  /** 원본 로그인이 sysadmin인가(모르면 null) */
  isSysadmin?: boolean | null
}
export interface SandboxStatus {
  state: SandboxState
  baselineAt: Iso | null
  fingerprintMatch: boolean
  locked: boolean
  manifest: { coveredTables: number; excludedTables: number; auditNotLast: number } | null
  mode?: ConnectionMode
  origin?: SandboxOrigin | null
  /** 경고(적용은 막지 않는다) */
  fingerprintWarnings?: string[]
  /** 차단(원본과 샌드박스의 환경이 달라 미리보기·적용이 막히는 항목) */
  fingerprintMismatches?: string[]
  /** 원본이 샌드박스 기준선 뒤에 바뀌어 기준선이 오래됐다 */
  baselineStale?: boolean
  /** false면 원본에 연결할 수 없다(미리보기는 기존 기준선으로 가능, 적용은 ORIGIN_UNREACHABLE) */
  originReachable?: boolean
  /** 샌드박스 인스턴스 설정 경고(xp_cmdshell 등 위험 설정 켜짐, 연결된 서버 있음). 막지 않는다. 옛 응답에는 없다. */
  instanceWarnings?: string[]
}
// POST /api/sandbox/refresh → 202, 본문 없음(Location: /api/sandbox)

// GET /api/catalog/schema → 200 | 409
export interface CatalogSchema {
  database: string
  baselineAt: Iso
  manifestVersion: string
  tables: SchemaTable[]
}
export interface SchemaTable {
  schema: string
  name: string
  table: string
  keyKind: KeyKind
  keyColumns: string[]
  covered: boolean
  excludedReason: string | null
  columns: SchemaColumn[]
}
export interface SchemaColumn {
  name: string
  /** "decimal(19,4)" 또는 "decimal" */
  sqlType: string
  isNullable: boolean
  isIdentity: boolean
  isComputed: boolean
  isRowVersion: boolean
  volatile: VolatileReason | null
  unobservable: boolean
}

// POST /api/analyze  body { sql: string } → 200 | 400 | 409
export interface Diagnostic {
  statementIndex: number | null
  span: SourceSpan
  severity: 'error' | 'warning'
  ruleId: string
  message: string
  docRef: string | null
}
export interface AnalyzedStatement {
  index: number
  batch: number
  position: number
  kind: StatementKind
  /** "스키마.테이블". EXEC면 null(표시는 procedure) */
  target: string | null
  /** EXEC: 부른 프로시저 "스키마.이름" */
  procedure?: string | null
  /** EXEC: 매개변수 원문 목록(표시용, 예 "@id = 5") */
  parameters?: string[]
  span: SourceSpan
  sql: string
  nondeterministicColumns: string[]
}
export interface AnalysisResult {
  statements: AnalyzedStatement[]
  rejects: Diagnostic[]
  warnings: Diagnostic[]
  diagnostics: Diagnostic[]
  isAccepted: boolean
}

// POST /api/previews  body { sql: string; options?: { imageCap: number | null } | null } → 200 PreviewResult | 400 | 409 | 422 | 500 | 503
// GET  /api/previews/{id} → 200 PreviewResult | 404
export interface PreviewRequest {
  sql: string
  options?: { imageCap: number | null } | null
}
export interface Fingerprint {
  productVersion: string
  edition: string
  compatibilityLevel: number
  isRecursiveTriggersOn: boolean
  nestedTriggers: number
  disallowResultsFromTriggers: number
  sessionOptions: number
  collation: string
  /** N4 계약 §5(없으면 null 또는 필드 없음) */
  serverCollation?: string | null
  hostPlatform?: string | null
  majorVersion?: number | null
  language?: string | null
  dateFormat?: string | null
  dateFirst?: number | null
  hasUserClrAssemblies?: boolean | null
}
export interface StatementError {
  errorNumber: number
  message: string
  procedure: string | null
  isToolFault: boolean
}
export type StatementStatus = 'ok' | 'failed' | 'trigger_blocked' | 'not_run'
export interface StatementResult {
  index: number
  batch: number
  position: number
  kind: StatementKind
  /** "스키마.테이블". EXEC면 null(프로시저 이름은 procedure). */
  target: string | null
  /** EXEC: "스키마.프로시저"(2026-11-13 추가). DML이면 필드가 없다. 예전 서버 응답에는 없을 수 있어 화면은 graph의 procedureCall 노드로 보완한다. */
  procedure?: string | null
  span: SourceSpan
  status: StatementStatus
  /** EXEC는 늘 null */
  rowsAffected: number | null
  /** EXEC: 프로시저 반환 값(RETURN). 실패·미실행이면 null */
  returnValue?: number | null
  /** EXEC: 프로시저가 낸 결과 집합 수(내용은 버린다) */
  resultSetCount?: number | null
  captureSeqRange: [number, number] | null
  error: StatementError | null
}
export type FkAction = 'CASCADE' | 'SET_NULL' | 'SET_DEFAULT'
export interface ForeignKeyCause {
  name: string
  parent: string
  child: string
  action: FkAction
  event: 'DELETE' | 'UPDATE'
  matchedRows: number
  unmatchedRows: number
}
export interface Cause {
  kind: 'DIRECT' | 'PROC_DIRECT' | 'FK_CASCADE' | 'FK_SET_NULL' | 'FK_SET_DEFAULT' | 'TRIGGER' | 'INSTEAD_OF' | 'UNEXPLAINED'
  confidence: Confidence
  parentFiringId: string | null
  foreignKey: ForeignKeyCause | null
  triggerCandidates: string[]
  evidence: Evidence[]
}
export interface Firing {
  id: string
  captureSeq: number
  statementIndex: number
  depth: number
  table: string
  operation: Op
  rowCount: number
  imageMode: 'FULL' | 'KEYS'
  auditOrder: 'LAST' | 'NONE'
  cause: Cause
  orderGuarantee: OrderGuarantee
}
export type ResidualCategory = 'EXCLUDED_TABLE' | 'UNOBSERVABLE_COLUMN' | 'SAME_TABLE_TRIGGER' | 'UNKNOWN'
export interface Residual {
  id: string
  table: string
  key: RowKey
  columns: string[]
  detectedBy: 'REREAD' | 'NET_DIFF'
  candidates: string[]
  category: ResidualCategory
  confidence: Confidence
  rowChangeId: string | null
}
export type NetChangeKind = 'INSERTED' | 'DELETED' | 'UPDATED' | 'MULTISET_REMOVED' | 'MULTISET_ADDED'
export interface NetChange {
  table: string
  key: RowKey
  change: NetChangeKind
  multiplicity: number
  before: Values | null
  after: Values | null
  changedColumns: string[]
  explainedBy: { firingIds: string[]; residualId: string | null } | null
}
export interface NetSummaryRow {
  table: string
  inserted: number
  updated: number
  deleted: number
  multisetRemoved: number
  multisetAdded: number
}
export type GraphNode =
  | {
      type: 'statement'
      id: string
      statementIndex: number
      label: string
      rowsAffected: number | null
      status: StatementResult['status']
    }
  | {
      /** EXEC 문장 노드(statement 노드 대신). id는 "st-{n}" */
      type: 'procedureCall'
      id: string
      statementIndex: number
      /** "EXEC 스키마.이름" */
      label: string
      /** "스키마.이름" */
      procedure: string
      status: StatementResult['status']
      /** 실패·미실행이면 null */
      returnValue: number | null
      resultSetCount: number | null
      /** 펼친 안쪽 프로시저 "스키마.이름" */
      expandedCalls: string[]
      /** 정적 쓰기 대상 "스키마.테이블"(안쪽 호출 포함) */
      writeTargets: string[]
      /** writeTargets 가운데 이 문장에서 묶음이 관측되지 않은 테이블(부분집합) */
      unobservedWriteTargets: string[]
    }
  | {
      type: 'firing'
      id: string
      firingId: string
      table: string
      operation: Op
      rowCount: number
      depth: number
      captureSeq: number
    }
  | {
      type: 'trigger'
      id: string
      trigger: string
      parentFiringId: string | null
      statementIndex: number
      observed: boolean
    }
  | { type: 'residual'; id: string; residualId: string; table: string; category: Residual['category'] }
  | { type: 'error'; id: string; errorNumber: number; message: string; isToolFault: boolean }
  | { type: 'group'; id: string; members: string[]; rowCount: number }
export type GraphEdgeKind = 'DIRECT' | 'PROC_DIRECT' | 'FK' | 'FIRES' | 'REPLACES' | 'WRITES' | 'RESIDUAL' | 'BLOCKED'
export interface GraphEdge {
  id: string
  source: string
  target: string
  kind: GraphEdgeKind
  confidence: Confidence
  orderGuarantee: OrderGuarantee
  evidence: Evidence[]
  fk: string | null
  action: ForeignKeyCause['action'] | null
  matchedRows: number | null
}
export interface GraphDto {
  nodes: GraphNode[]
  edges: GraphEdge[]
}
export type PreviewStatus = 'ok' | 'failed' | 'trigger_blocked' | 'truncated'
export interface PreviewResult {
  previewId: string
  status: PreviewStatus
  createdAt: Iso
  sqlHash: string
  baseline: {
    originDb: string
    sandboxDb: string
    snapshotDb: string
    backupFinishedAt: Iso
    fingerprint: Fingerprint
    /** N4 계약 §5 */
    fingerprintWarnings?: string[]
  }
  statements: StatementResult[]
  firings: Firing[]
  residuals: Residual[]
  residualCommitted: NetChange[]
  /** 3주차 추가(인계 7) */
  residualCheckFailed: boolean
  residualCheckMessage: string | null
  netSummary: NetSummaryRow[]
  graph: GraphDto
  warnings: Diagnostic[]
  capture: { imageCount: number; cap: number; truncated: boolean; manifestVersion: string }
  timingsMs: {
    analyze: number
    execute: number
    readLog: number
    reread: number
    netDiff: number
    resolve: number
    serialize: number
  }
}

// GET /api/previews/{id}/rows?view=event|net&firingId=&offset=0&limit=100 (limit 1..1000) → 200 | 400 | 404
export type PairingKind = 'BY_PK' | 'BY_UNIQUE' | 'SINGLE_ROW' | 'UNPAIRED'
export interface RowChange {
  id: string
  firingId: string
  operation: Op
  key: RowKey
  before: Values | null
  after: Values | null
  changedColumns: string[]
  volatile: { column: string; reason: VolatileReason }[]
  derived: string[]
  pairing: PairingKind
  finalAfterSource: 'AUDIT' | 'REREAD'
  parentRowIds: string[]
}
export type RowView = 'event' | 'net'
/** event → RowChange, net → NetChange */
export interface RowPage<T> {
  total: number
  view: RowView
  offset: number
  limit: number
  rows: T[]
}

// POST /api/previews/{id}/apply  body(선택) { sqlHash: string | null } → 200 ApplyResult | 404 | 409 | 500 | 503
export type ApplyPrecondition =
  | 'PREVIEW_NOT_OK'
  | 'SQL_HASH_MISMATCH'
  | 'PREVIEW_EXPIRED'
  | 'FINGERPRINT_MISMATCH'
  | 'EXECUTE_AS_USER_MISSING'
  | 'WEAK_VERIFICATION_NOT_ACCEPTED'
  | 'ORIGIN_UNREACHABLE'
  /** 미리보기에 EXEC 문장이 있어 적용할 수 없다(EXEC는 미리보기 전용, 서버 쪽은 오케스트레이터가 넣는 중) */
  | 'PROC_APPLY_NOT_SUPPORTED'
export type ApplyStatus = 'committed' | 'rolled_back_mismatch' | 'drift_detected' | 'precondition_failed' | 'error'
export interface ApplyRequest {
  sqlHash: string | null
  /** 원본에 스냅샷 권한이 없어 KEYSET_HASH(약한 검증)가 필요할 때 사용자가 동의했다(기본 false) */
  allowKeysetVerification?: boolean
}
export interface DriftRow {
  table: string
  key: RowKey
  kind: 'CHANGED' | 'MISSING' | 'MULTISET_COUNT'
  columns: string[]
  expected: Values | null
  actual: Values | null
  expectedMultiplicity: number | null
  actualMultiplicity: number | null
}
export type MismatchKind =
  | 'STATEMENT_ROW_COUNT'
  | 'NET_SUMMARY'
  | 'ROW_VALUE'
  | 'ROW_MISSING'
  | 'ROW_UNEXPECTED'
  | 'IDENTITY_UNPAIRED'
  | 'UNEXPECTED_TABLE'
  | 'VOLATILE_FORMAT'
  | 'KEYSET_TABLE_COUNT'
  /** KEYSET_HASH: 미리보기에 없던 테이블의 COUNT_BIG·CHECKSUM_AGG가 실행 전후로 달라짐 */
  | 'OUTSIDE_TABLE_CHANGED'
export interface ApplyMismatch {
  kind: MismatchKind
  statementIndex: number | null
  table: string | null
  key: RowKey | null
  column: string | null
  expected: string | null
  actual: string | null
  message: string
}
export interface ApplyVerification {
  mode: 'SNAPSHOT_NETDIFF' | 'KEYSET_HASH'
  driftRows: DriftRow[]
  statementRowCounts: { index: number; preview: number | null; actual: number | null }[]
  netSummaryMatch: boolean | null
  actualNetSummary: NetSummaryRow[]
  mismatches: ApplyMismatch[]
  /** "스키마.테이블.열" */
  volatileIgnored: string[]
  identityPairs: { table: string; previewKey: RowKey; actualKey: RowKey }[]
  residualCommitted: NetChange[]
  residualCheckFailed: boolean
  tableCounts: { table: string; before: number; after: number; expectedDelta: number }[]
  limitations: string[]
}
export interface ApplyResult {
  previewId: string
  status: ApplyStatus
  finishedAt: Iso
  preconditionFailures: ApplyPrecondition[]
  verification: ApplyVerification
  statements: StatementResult[]
  message: string | null
  sandboxRefreshStarted: boolean
  timingsMs: { snapshot: number; drift: number; execute: number; netDiff: number; commit: number }
}

// POST /api/previews/{id}/cancel → 204 | 404
// GET  /api/previews/{id}/export → 200 application/json 파일
export interface PreviewExport {
  result: PreviewResult
  rowChanges: RowChange[]
  netChanges: NetChange[]
  sql: string
}

// ---- 설정(프로필) API, N4 계약 §4. 비밀번호·연결 문자열은 응답에 없다. ----

export type SettingsSource = 'profile' | 'env' | 'none'
export type AuthKind = 'windows' | 'sql'

/** GET /api/settings → 200 */
export interface SettingsEndpointView {
  server: string
  database: string
  auth: AuthKind
  user: string | null
  hasStoredPassword: boolean
}
export interface SettingsOriginView extends SettingsEndpointView {
  trustServerCertificate: boolean
}
export interface SettingsBackup {
  serverPath: string | null
  sharePath: string | null
  importFile: string | null
}
export interface SettingsView {
  source: SettingsSource
  configured: boolean
  mode?: ConnectionMode
  origin?: SettingsOriginView | null
  sandbox?: SettingsEndpointView | null
  backup?: SettingsBackup | null
}

/** PUT /api/settings · POST /api/settings/test 본문. 비밀번호는 바꿀 때만 넣는다. */
export interface SettingsEndpointInput {
  server: string
  database: string
  auth: AuthKind
  user: string | null
  password?: string
  rememberPassword: boolean
}
export interface SettingsRequest {
  mode: ConnectionMode
  origin: SettingsEndpointInput & { trustServerCertificate: boolean }
  sandbox: SettingsEndpointInput
  backup: SettingsBackup
}

/** POST /api/settings/test → 200 */
export interface SettingsTestResult {
  origin: {
    ok: boolean
    serverName?: string | null
    productVersion?: string | null
    edition?: string | null
    isSysadmin?: boolean | null
    canBackup?: boolean | null
    canCreateDatabase?: boolean | null
    error: string | null
  } | null
  sandbox: {
    ok: boolean
    serverName?: string | null
    productVersion?: string | null
    isLoopback?: boolean | null
    error: string | null
  } | null
  warnings?: string[]
}
