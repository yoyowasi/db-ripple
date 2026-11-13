// 개발 전용 시험 데이터(백엔드 없이 화면 확인). 운영 빌드에는 들어가지 않는다(api/client.ts 의 DEV 분기에서만 동적 import).
// 실측 근거: logs/e2e/host-e2e-2026-11-12.log 6줄(DM1)·16~21줄(DM2)·22~24줄(DM3)·26~29줄(DM7)·47줄(DM1 화면),
// logs/e2e/sql-dm2-after.log(DM2 이전→이후), scripts/demo/demo.http(SQL).
// 로그에 없는 값은 "예시값" 주석을 달고, 화면 아래 개발 표시(FixtureNote)에도 같은 내용을 적는다.
import type {
  AnalysisResult,
  AnalyzedStatement,
  ApplyRequest,
  ApplyResult,
  ApplyVerification,
  CatalogSchema,
  Cause,
  Diagnostic,
  Firing,
  GraphDto,
  GraphEdge,
  GraphNode,
  NetChange,
  NetSummaryRow,
  Op,
  PreviewResult,
  ProblemDetails,
  RowChange,
  RowKey,
  SandboxStatus,
  SettingsView,
  SchemaColumn,
  SchemaTable,
  SourceSpan,
  StatementResult,
  Values,
  VolatileReason,
} from '../api/types'

export const FIXTURE_IDS = [
  'dm1',
  'dm2',
  'dm3',
  'dm6',
  'dm7',
  'dm2c',
  'rej',
  'building',
  // N4(설정·분리 모드·원본 표시)
  'split',
  'stale',
  'weak',
  'unreachable',
  'mismatch',
  'outside',
  'first',
  'settings',
  'env',
  'nosession',
  // EXEC(프로시저 호출) 미리보기 · 미리보기 전용
  'exec',
  'execrej',
  'execrb',
] as const
export type FixtureId = (typeof FIXTURE_IDS)[number]

/** 페이지를 서버 대신 만들어 주는 행 원천. 큰 결과(DM6)는 배열 대신 생성 함수로 둔다. */
export type RowSource<T> = T[] | { firingIds: string[]; total: number; make: (i: number) => T }

export interface Fixture {
  id: FixtureId
  label: string
  /** 실측 근거(화면 개발 표시에 그대로 보인다) */
  source: string
  /** 로그에 없어 채운 값 */
  examples: string
  sql: string
  imageCap: number | null
  /** 화면을 연 뒤 미리보기를 자동으로 실행한다 */
  autoRun: boolean
  /** 미리보기 뒤 적용까지 자동으로 누른다(적용 결과 화면 확인용) */
  autoApply: boolean
  sandbox: SandboxStatus
  /** null이면 409 BUILDING */
  schema: Omit<CatalogSchema, 'baselineAt'> | null
  analysis: AnalysisResult
  /** 성공이면 PreviewResult(만든 시각을 받는다), 아니면 ProblemDetails */
  preview: ((createdAt: string) => PreviewResult) | ProblemDetails
  eventRows: RowSource<RowChange>
  netRows: RowSource<NetChange>
  apply: ((finishedAt: string, previewId: string, req: ApplyRequest) => ApplyResult) | null
  /** 설정 API의 처음 응답(없으면 설정이 끝난 프로필 예시) */
  settings?: SettingsView
  /** 있으면 설정 API 밖의 모든 요청이 이 오류로 끝난다(첫 실행: 연결 설정이 없어 503) */
  sandboxProblem?: ProblemDetails
}

// ---- 공통 값 ----

/** 목업 v6 띠의 "기준 시각 2026-11-12 14:27"(예시값). */
export const BASELINE_AT = '2026-11-12T14:27:00+09:00'

const READY: SandboxStatus = {
  state: 'ready',
  baselineAt: BASELINE_AT,
  fingerprintMatch: true,
  locked: false,
  // 예시값: 목업 탐색기 "전체 71개"
  manifest: { coveredTables: 71, excludedTables: 0, auditNotLast: 0 },
}

// 예시값: 목업 v6 띠 tooltip(16.0.4175 · Enterprise · compat 160). 나머지 항목도 예시값.
const FINGERPRINT = {
  productVersion: '16.0.4175.1',
  edition: 'Enterprise Edition (64-bit)',
  compatibilityLevel: 160,
  isRecursiveTriggersOn: false,
  nestedTriggers: 1,
  disallowResultsFromTriggers: 0,
  sessionOptions: 5496,
  collation: 'SQL_Latin1_General_CP1_CI_AS',
}

function line1(sql: string, line = 1): SourceSpan {
  return { startLine: line, startColumn: 1, endLine: line, endColumn: sql.length + 1 }
}

function analyzed(index: number, kind: Op, target: string, sql: string, line = index): AnalyzedStatement {
  return { index, batch: 1, position: index, kind, target, span: line1(sql, line), sql, nondeterministicColumns: [] }
}

function accepted(...statements: AnalyzedStatement[]): AnalysisResult {
  return { statements, rejects: [], warnings: [], diagnostics: [], isAccepted: true }
}

function stmtResult(
  a: AnalyzedStatement,
  status: StatementResult['status'],
  rowsAffected: number | null,
  captureSeqRange: [number, number] | null,
  error: StatementResult['error'] = null,
): StatementResult {
  const { index, batch, position, kind, target, span } = a
  return { index, batch, position, kind, target, span, status, rowsAffected, captureSeqRange, error }
}

const direct = (): Cause => ({
  kind: 'DIRECT',
  confidence: 'certain',
  parentFiringId: null,
  foreignKey: null,
  triggerCandidates: [],
  evidence: ['depth'],
})

/** EXEC 문장 창 안, 깊이 1, FK로 설명되지 않는 묶음 = 프로시저 본문의 직접 쓰기(계약 §5). */
const procDirect = (): Cause => ({
  kind: 'PROC_DIRECT',
  confidence: 'certain',
  parentFiringId: null,
  foreignKey: null,
  triggerCandidates: [],
  evidence: ['depth', 'staticWriteSet'],
})

const fkCascade = (parentFiringId: string, name: string, parent: string, child: string, matchedRows: number): Cause => ({
  kind: 'FK_CASCADE',
  confidence: 'certain',
  parentFiringId,
  foreignKey: { name, parent, child, action: 'CASCADE', event: 'DELETE', matchedRows, unmatchedRows: 0 },
  triggerCandidates: [],
  evidence: ['fkKeyMatch'],
})

// 근거 목록은 예시값(로그에는 cause 종류·확신도만 있다).
const trig = (parentFiringId: string, trigger: string): Cause => ({
  kind: 'TRIGGER',
  confidence: 'inferred',
  parentFiringId,
  foreignKey: null,
  triggerCandidates: [trigger],
  evidence: ['depth', 'staticWriteSet'],
})

function firing(
  id: string,
  captureSeq: number,
  depth: number,
  table: string,
  operation: Op,
  rowCount: number,
  cause: Cause,
  imageMode: Firing['imageMode'] = 'FULL',
): Firing {
  return { id, captureSeq, statementIndex: 1, depth, table, operation, rowCount, imageMode, auditOrder: 'LAST', cause, orderGuarantee: 'guaranteed' }
}

function stNode(s: StatementResult, sql: string): GraphNode {
  return { type: 'statement', id: `st-${s.index}`, statementIndex: s.index, label: sql.replace(/;$/, ''), rowsAffected: s.rowsAffected, status: s.status }
}

function fNode(f: Firing): GraphNode {
  return { type: 'firing', id: f.id, firingId: f.id, table: f.table, operation: f.operation, rowCount: f.rowCount, depth: f.depth, captureSeq: f.captureSeq }
}

function tNode(id: string, trigger: string, parentFiringId: string, observed: boolean): GraphNode {
  return { type: 'trigger', id, trigger, parentFiringId, statementIndex: 1, observed }
}

let edgeSeq = 0
function edge(source: string, target: string, kind: GraphEdge['kind'], confidence: GraphEdge['confidence'], fk?: { name: string; matched: number }): GraphEdge {
  return {
    id: `e${++edgeSeq}`,
    source,
    target,
    kind,
    confidence,
    orderGuarantee: 'guaranteed',
    evidence: kind === 'FK' ? ['fkKeyMatch'] : kind === 'DIRECT' || kind === 'PROC_DIRECT' ? ['depth'] : [],
    fk: fk?.name ?? null,
    action: fk ? 'CASCADE' : null,
    matchedRows: fk?.matched ?? null,
  }
}

const pk = (columns: string[], values: string[]): RowKey => ({ kind: 'PK', columns, values })

function rowChange(
  id: string,
  firingId: string,
  operation: Op,
  key: RowKey,
  before: Values | null,
  after: Values | null,
  o: Partial<Pick<RowChange, 'changedColumns' | 'volatile' | 'derived' | 'parentRowIds'>> = {},
): RowChange {
  return {
    id,
    firingId,
    operation,
    key,
    before,
    after,
    changedColumns: o.changedColumns ?? [],
    volatile: o.volatile ?? [],
    derived: o.derived ?? [],
    pairing: 'BY_PK',
    finalAfterSource: 'AUDIT',
    parentRowIds: o.parentRowIds ?? [],
  }
}

function net(
  table: string,
  key: RowKey,
  change: NetChange['change'],
  before: Values | null,
  after: Values | null,
  changedColumns: string[],
  firingIds: string[],
): NetChange {
  return { table, key, change, multiplicity: 1, before, after, changedColumns, explainedBy: { firingIds, residualId: null } }
}

const sum = (table: string, i: number, u: number, d: number): NetSummaryRow => ({
  table,
  inserted: i,
  updated: u,
  deleted: d,
  multisetRemoved: 0,
  multisetAdded: 0,
})

interface PreviewParts {
  previewId: string
  status: PreviewResult['status']
  sqlHash: string
  statements: StatementResult[]
  firings: Firing[]
  netSummary: NetSummaryRow[]
  graph: GraphDto
  totalMs: number
  capture?: PreviewResult['capture']
  originDb?: string
  warnings?: Diagnostic[]
}

function previewOf(p: PreviewParts): (createdAt: string) => PreviewResult {
  // 단계별 소요는 예시값(로그에는 전체 시간만 있다): 실행 55% · 로그 읽기 20% · 나머지 나눔.
  const t = p.totalMs
  const execute = Math.round(t * 0.55)
  const readLog = Math.round(t * 0.2)
  const netDiff = Math.round(t * 0.12)
  const reread = Math.round(t * 0.07)
  const analyze = Math.round(t * 0.02)
  const resolve = Math.round(t * 0.03)
  const serialize = t - execute - readLog - netDiff - reread - analyze - resolve
  return (createdAt) => ({
    previewId: p.previewId,
    status: p.status,
    createdAt,
    sqlHash: p.sqlHash,
    baseline: {
      originDb: p.originDb ?? 'AW_Origin',
      sandboxDb: (p.originDb ?? 'AW') + '_Sandbox', // 예시값
      snapshotDb: (p.originDb ?? 'AW') + '_Snapshot', // 예시값
      backupFinishedAt: BASELINE_AT,
      fingerprint: FINGERPRINT,
    },
    statements: p.statements,
    firings: p.firings,
    residuals: [],
    residualCommitted: [],
    residualCheckFailed: false,
    residualCheckMessage: null,
    netSummary: p.netSummary,
    graph: p.graph,
    warnings: p.warnings ?? [],
    capture: p.capture ?? { imageCount: p.firings.reduce((a, f) => a + f.rowCount, 0), cap: 100000, truncated: false, manifestVersion: 'm-20261112-1427' },
    timingsMs: { analyze, execute, readLog, reread, netDiff, resolve, serialize },
  })
}

function verification(o: Partial<ApplyVerification> = {}): ApplyVerification {
  return {
    mode: 'SNAPSHOT_NETDIFF',
    driftRows: [],
    statementRowCounts: [],
    netSummaryMatch: null,
    actualNetSummary: [],
    mismatches: [],
    volatileIgnored: [],
    identityPairs: [],
    residualCommitted: [],
    residualCheckFailed: false,
    tableCounts: [],
    limitations: [],
    ...o,
  }
}

const ZERO_APPLY_TIMINGS = { snapshot: 0, drift: 0, execute: 0, netDiff: 0, commit: 0 }

function precondition(message: string) {
  return (finishedAt: string, previewId: string): ApplyResult => ({
    previewId,
    status: 'precondition_failed',
    finishedAt,
    preconditionFailures: ['PREVIEW_NOT_OK'],
    verification: verification(),
    statements: [],
    message,
    sandboxRefreshStarted: false,
    timingsMs: ZERO_APPLY_TIMINGS,
  })
}

function committed(statements: StatementResult[], netSummary: NetSummaryRow[], extra: Partial<ApplyVerification>, timingsMs: ApplyResult['timingsMs']) {
  return (finishedAt: string, previewId: string): ApplyResult => ({
    previewId,
    status: 'committed',
    finishedAt,
    preconditionFailures: [],
    verification: verification({
      statementRowCounts: statements.map((s) => ({ index: s.index, preview: s.rowsAffected, actual: s.rowsAffected })),
      netSummaryMatch: true,
      actualNetSummary: netSummary,
      ...extra,
    }),
    statements,
    message: null,
    sandboxRefreshStarted: true,
    timingsMs,
  })
}

// ---- 카탈로그(AdventureWorks 일부). 열 형식은 AW 기준 예시값, 열은 시험 행에 나오는 것만. ----

function col(name: string, sqlType: string, o: { nullable?: boolean; identity?: boolean; computed?: boolean; volatile?: VolatileReason } = {}): SchemaColumn {
  return {
    name,
    sqlType,
    isNullable: o.nullable ?? false,
    isIdentity: o.identity ?? false,
    isComputed: o.computed ?? false,
    isRowVersion: false,
    volatile: o.volatile ?? null,
    unobservable: false,
  }
}

function tbl(schema: string, name: string, keyColumns: string[], columns: SchemaColumn[]): SchemaTable {
  return { schema, name, table: `${schema}.${name}`, keyKind: 'PK', keyColumns, covered: true, excludedReason: null, columns }
}

// DM7 driftRows(로그 29줄)의 열 이름. 형식은 예시값.
const PRODUCT_COLUMNS: [string, string][] = [
  ['ProductID', 'int'],
  ['Name', 'nvarchar(50)'],
  ['ProductNumber', 'nvarchar(25)'],
  ['MakeFlag', 'bit'],
  ['FinishedGoodsFlag', 'bit'],
  ['Color', 'nvarchar(15)'],
  ['SafetyStockLevel', 'smallint'],
  ['ReorderPoint', 'smallint'],
  ['StandardCost', 'money'],
  ['ListPrice', 'money'],
  ['Size', 'nvarchar(5)'],
  ['SizeUnitMeasureCode', 'nchar(3)'],
  ['WeightUnitMeasureCode', 'nchar(3)'],
  ['Weight', 'decimal(8,2)'],
  ['DaysToManufacture', 'int'],
  ['ProductLine', 'nchar(2)'],
  ['Class', 'nchar(2)'],
  ['Style', 'nchar(2)'],
  ['ProductSubcategoryID', 'int'],
  ['ProductModelID', 'int'],
  ['SellStartDate', 'datetime'],
  ['SellEndDate', 'datetime'],
  ['DiscontinuedDate', 'datetime'],
  ['rowguid', 'uniqueidentifier'],
  ['ModifiedDate', 'datetime'],
]

const AW_SCHEMA: Omit<CatalogSchema, 'baselineAt'> = {
  database: 'AdventureWorks',
  manifestVersion: 'm-20261112-1427', // 예시값
  tables: [
    tbl('HumanResources', 'Employee', ['BusinessEntityID'], [col('BusinessEntityID', 'int'), col('JobTitle', 'nvarchar(50)'), col('HireDate', 'date')]),
    tbl('HumanResources', 'EmployeePayHistory', ['BusinessEntityID', 'RateChangeDate'], [
      col('BusinessEntityID', 'int'),
      col('RateChangeDate', 'datetime'),
      col('Rate', 'money'),
    ]),
    tbl('Person', 'Person', ['BusinessEntityID'], [col('BusinessEntityID', 'int'), col('FirstName', 'nvarchar(50)'), col('Demographics', 'xml', { nullable: true })]),
    tbl('Production', 'Product', ['ProductID'], PRODUCT_COLUMNS.map(([n, t]) => col(n, t, { identity: n === 'ProductID', nullable: /^(Color|Size|Weight|SellEndDate|DiscontinuedDate)/.test(n) }))),
    tbl('Production', 'TransactionHistory', ['TransactionID'], [
      col('TransactionID', 'int', { identity: true, volatile: 'IDENTITY' }),
      col('ProductID', 'int'),
      col('ReferenceOrderID', 'int'),
      col('ReferenceOrderLineID', 'int'),
      col('TransactionDate', 'datetime', { volatile: 'NONDETERMINISTIC_DEFAULT' }),
      col('TransactionType', 'nchar(1)'),
      col('Quantity', 'int'),
      col('ActualCost', 'money'),
      col('ModifiedDate', 'datetime', { volatile: 'NONDETERMINISTIC_DEFAULT' }),
    ]),
    tbl('Sales', 'Customer', ['CustomerID'], [col('CustomerID', 'int', { identity: true }), col('StoreID', 'int', { nullable: true })]),
    tbl('Sales', 'SalesOrderDetail', ['SalesOrderID', 'SalesOrderDetailID'], [
      col('SalesOrderID', 'int'),
      col('SalesOrderDetailID', 'int', { identity: true }),
      col('OrderQty', 'smallint'),
      col('LineTotal', 'numeric(38,6)', { computed: true }),
    ]),
    tbl('Sales', 'SalesOrderHeader', ['SalesOrderID'], [
      col('SalesOrderID', 'int', { identity: true }),
      col('RevisionNumber', 'tinyint'),
      col('Status', 'tinyint'),
      col('CustomerID', 'int'),
      col('SubTotal', 'money'),
      col('TotalDue', 'money', { computed: true }),
    ]),
    tbl('Sales', 'SalesOrderHeaderSalesReason', ['SalesOrderID', 'SalesReasonID'], [col('SalesOrderID', 'int'), col('SalesReasonID', 'int')]),
    tbl('Sales', 'SalesPerson', ['BusinessEntityID'], [col('BusinessEntityID', 'int'), col('SalesYTD', 'money')]),
    tbl('Sales', 'SalesTerritory', ['TerritoryID'], [col('TerritoryID', 'int', { identity: true }), col('SalesYTD', 'money')]),
    tbl('Sales', 'Store', ['BusinessEntityID'], [col('BusinessEntityID', 'int'), col('Name', 'nvarchar(50)')]),
  ],
}

// ---- DM1: DELETE SalesOrderHeader 43697 (로그 6줄·47줄) ----
// 묶음 id·captureSeq는 예시값(로그 나열 순서 = 수집 순서로 f-1~f-5). 부모 관계는 cause 종류와 FK 이름에서 정했다.
// 트리거 노드: iduSalesOrderDetail 관측, uSalesOrderHeader·iuPerson 관측 안 됨(47줄). 0행 묶음 → 트리거 연결은 추정.

const DM1_SQL = 'DELETE FROM Sales.SalesOrderHeader WHERE SalesOrderID = 43697;'
const DM1_A = analyzed(1, 'DELETE', 'Sales.SalesOrderHeader', DM1_SQL)
const FK_SOD = 'FK_SalesOrderDetail_SalesOrderHeader_SalesOrderID'
const FK_SOHSR = 'FK_SalesOrderHeaderSalesReason_SalesOrderHeader_SalesOrderID'
const DM1_F = [
  firing('f-1', 1, 1, 'Sales.SalesOrderHeaderSalesReason', 'DELETE', 2, fkCascade('f-5', FK_SOHSR, 'Sales.SalesOrderHeader', 'Sales.SalesOrderHeaderSalesReason', 2)),
  firing('f-2', 2, 2, 'Sales.SalesOrderHeader', 'UPDATE', 0, trig('f-4', 'Sales.iduSalesOrderDetail')),
  firing('f-3', 3, 2, 'Person.Person', 'UPDATE', 0, trig('f-4', 'Sales.iduSalesOrderDetail')),
  firing('f-4', 4, 1, 'Sales.SalesOrderDetail', 'DELETE', 1, fkCascade('f-5', FK_SOD, 'Sales.SalesOrderHeader', 'Sales.SalesOrderDetail', 1)),
  firing('f-5', 5, 1, 'Sales.SalesOrderHeader', 'DELETE', 1, direct()),
]
const DM1_STMT = stmtResult(DM1_A, 'ok', 1, [1, 5])
edgeSeq = 0
const DM1_GRAPH: GraphDto = {
  nodes: [
    stNode(DM1_STMT, DM1_SQL),
    ...DM1_F.map(fNode),
    tNode('t-1', 'Sales.iduSalesOrderDetail', 'f-4', true),
    tNode('t-2', 'Sales.uSalesOrderHeader', 'f-2', false),
    tNode('t-3', 'Person.iuPerson', 'f-3', false),
  ],
  edges: [
    edge('st-1', 'f-5', 'DIRECT', 'certain'),
    edge('f-5', 'f-4', 'FK', 'certain', { name: FK_SOD, matched: 1 }),
    edge('f-5', 'f-1', 'FK', 'certain', { name: FK_SOHSR, matched: 2 }),
    edge('f-4', 't-1', 'FIRES', 'inferred'),
    edge('t-1', 'f-2', 'WRITES', 'inferred'),
    edge('t-1', 'f-3', 'WRITES', 'inferred'),
    edge('f-2', 't-2', 'FIRES', 'inferred'),
    edge('f-3', 't-3', 'FIRES', 'inferred'),
  ],
}
const DM1_SUM = [sum('Sales.SalesOrderDetail', 0, 0, 1), sum('Sales.SalesOrderHeader', 0, 0, 1), sum('Sales.SalesOrderHeaderSalesReason', 0, 0, 2)]
// 행 값: 키 43697·353·사유 5/9, CustomerID 21768·Status 5는 목업 v6 근거(demo.http·SP8 로그). 그 밖의 열은 비워 둔다.
const DM1_EVENT: RowChange[] = [
  rowChange('rc-1', 'f-5', 'DELETE', pk(['SalesOrderID'], ['43697']), { SalesOrderID: '43697', CustomerID: '21768', Status: '5' }, null),
  rowChange('rc-2', 'f-4', 'DELETE', pk(['SalesOrderID', 'SalesOrderDetailID'], ['43697', '353']), { SalesOrderID: '43697', SalesOrderDetailID: '353' }, null, {
    parentRowIds: ['rc-1'],
  }),
  rowChange('rc-3', 'f-1', 'DELETE', pk(['SalesOrderID', 'SalesReasonID'], ['43697', '5']), { SalesOrderID: '43697', SalesReasonID: '5' }, null, {
    parentRowIds: ['rc-1'],
  }),
  rowChange('rc-4', 'f-1', 'DELETE', pk(['SalesOrderID', 'SalesReasonID'], ['43697', '9']), { SalesOrderID: '43697', SalesReasonID: '9' }, null, {
    parentRowIds: ['rc-1'],
  }),
]
const DM1_NET: NetChange[] = DM1_EVENT.map((r) => net(DM1_F.find((f) => f.id === r.firingId)!.table, r.key, 'DELETED', r.before, null, [], [r.firingId]))

// ---- DM2: UPDATE SalesOrderDetail 52 OrderQty+1 (로그 16~21줄, sql-dm2-after.log) ----
// 묶음 id f-13~f-24는 나열 순서와 net/event 행의 explainedBy로 맞췄다(f-13 TH, f-16·f-23 Person, f-17·f-20 SOH, f-24 SOD).
// 깊이 3 묶음의 부모는 로그에 없다. 감사 기록이 자식 먼저(후위) 쌓인다고 보고 바로 뒤 깊이 2 묶음을 부모로 정했다(예시값).

const DM2_SQL = 'UPDATE Sales.SalesOrderDetail SET OrderQty = OrderQty + 1 WHERE SalesOrderDetailID = 52;'
const DM2_A = analyzed(1, 'UPDATE', 'Sales.SalesOrderDetail', DM2_SQL)
const IDU = 'Sales.iduSalesOrderDetail'
const USOH = 'Sales.uSalesOrderHeader'
const IUP = 'Person.iuPerson'
const DM2_F = [
  firing('f-13', 13, 2, 'Production.TransactionHistory', 'INSERT', 1, trig('f-24', IDU)),
  firing('f-14', 14, 3, 'Person.Person', 'UPDATE', 0, trig('f-16', IUP)),
  firing('f-15', 15, 3, 'Person.Person', 'UPDATE', 0, trig('f-16', IUP)),
  firing('f-16', 16, 2, 'Person.Person', 'UPDATE', 1, trig('f-24', IDU)),
  firing('f-17', 17, 3, 'Sales.SalesOrderHeader', 'UPDATE', 1, trig('f-20', USOH)),
  firing('f-18', 18, 3, 'Sales.SalesPerson', 'UPDATE', 0, trig('f-20', USOH)),
  firing('f-19', 19, 3, 'Sales.SalesTerritory', 'UPDATE', 0, trig('f-20', USOH)),
  firing('f-20', 20, 2, 'Sales.SalesOrderHeader', 'UPDATE', 1, trig('f-24', IDU)),
  firing('f-21', 21, 3, 'Person.Person', 'UPDATE', 0, trig('f-23', IUP)),
  firing('f-22', 22, 3, 'Person.Person', 'UPDATE', 0, trig('f-23', IUP)),
  firing('f-23', 23, 2, 'Person.Person', 'UPDATE', 1, trig('f-24', IDU)),
  firing('f-24', 24, 1, 'Sales.SalesOrderDetail', 'UPDATE', 1, direct()),
]
const DM2_STMT = stmtResult(DM2_A, 'ok', 1, [13, 24])
edgeSeq = 0
const DM2_GRAPH: GraphDto = {
  nodes: [
    stNode(DM2_STMT, DM2_SQL),
    ...DM2_F.map(fNode),
    tNode('t-1', IDU, 'f-24', true),
    tNode('t-2', IUP, 'f-16', false),
    tNode('t-3', USOH, 'f-20', true),
    tNode('t-4', IUP, 'f-23', false),
  ],
  edges: [
    edge('st-1', 'f-24', 'DIRECT', 'certain'),
    edge('f-24', 't-1', 'FIRES', 'inferred'),
    ...['f-13', 'f-16', 'f-20', 'f-23'].map((t) => edge('t-1', t, 'WRITES', 'inferred')),
    edge('f-16', 't-2', 'FIRES', 'inferred'),
    ...['f-14', 'f-15'].map((t) => edge('t-2', t, 'WRITES', 'inferred')),
    edge('f-20', 't-3', 'FIRES', 'inferred'),
    ...['f-17', 'f-18', 'f-19'].map((t) => edge('t-3', t, 'WRITES', 'inferred')),
    edge('f-23', 't-4', 'FIRES', 'inferred'),
    ...['f-21', 'f-22'].map((t) => edge('t-4', t, 'WRITES', 'inferred')),
  ],
}
const DM2_SUM = [
  sum('Person.Person', 0, 1, 0),
  sum('Production.TransactionHistory', 1, 0, 0),
  sum('Sales.SalesOrderDetail', 0, 1, 0),
  sum('Sales.SalesOrderHeader', 0, 1, 0),
]
// 이전→이후는 sql-dm2-after.log(AW_Origin → e2e). Demographics는 XML이라 TotalPurchaseYTD 값만 적는다.
// 같은 행을 두 묶음이 나눠 바꾼 중간값(Person -4206.2024, SOH 묶음별 열 나눔)과 TransactionDate·ModifiedDate는 예시값.
const TH_ROW: Values = {
  TransactionID: '213443',
  ProductID: '760',
  ReferenceOrderID: '43663',
  ReferenceOrderLineID: '52',
  TransactionDate: '2026-11-12T14:28:38.997',
  TransactionType: 'S',
  Quantity: '2',
  ActualCost: '419.4589',
  ModifiedDate: '2026-11-12T14:28:38.997',
}
const PERSON_KEY = pk(['BusinessEntityID'], ['483'])
const SOH_KEY = pk(['SalesOrderID'], ['43663'])
const SOD_KEY = pk(['SalesOrderID', 'SalesOrderDetailID'], ['43663', '52'])
const DM2_EVENT: RowChange[] = [
  rowChange('rc-1', 'f-24', 'UPDATE', SOD_KEY, { SalesOrderID: '43663', SalesOrderDetailID: '52', OrderQty: '1', LineTotal: '419.458900' }, {
    SalesOrderID: '43663',
    SalesOrderDetailID: '52',
    OrderQty: '2',
    LineTotal: '838.917800',
  }, { changedColumns: ['OrderQty', 'LineTotal'], derived: ['LineTotal'] }),
  rowChange('rc-2', 'f-13', 'INSERT', pk(['TransactionID'], ['213443']), null, TH_ROW, {
    volatile: [
      { column: 'TransactionID', reason: 'IDENTITY' },
      { column: 'TransactionDate', reason: 'NONDETERMINISTIC_DEFAULT' },
      { column: 'ModifiedDate', reason: 'NONDETERMINISTIC_DEFAULT' },
    ],
    parentRowIds: ['rc-1'],
  }),
  rowChange('rc-3', 'f-16', 'UPDATE', PERSON_KEY, { BusinessEntityID: '483', Demographics: 'TotalPurchaseYTD -4416.2024' }, {
    BusinessEntityID: '483',
    Demographics: 'TotalPurchaseYTD -4206.2024',
  }, { changedColumns: ['Demographics'], parentRowIds: ['rc-1'] }),
  rowChange('rc-4', 'f-20', 'UPDATE', SOH_KEY, { SalesOrderID: '43663', SubTotal: '419.4589', TotalDue: '472.3108' }, {
    SalesOrderID: '43663',
    SubTotal: '838.9178',
    TotalDue: '891.7697',
  }, { changedColumns: ['SubTotal', 'TotalDue'], derived: ['TotalDue'], parentRowIds: ['rc-1'] }),
  rowChange('rc-5', 'f-17', 'UPDATE', SOH_KEY, { SalesOrderID: '43663', RevisionNumber: '10' }, { SalesOrderID: '43663', RevisionNumber: '11' }, {
    changedColumns: ['RevisionNumber'],
    parentRowIds: ['rc-4'],
  }),
  rowChange('rc-6', 'f-23', 'UPDATE', PERSON_KEY, { BusinessEntityID: '483', Demographics: 'TotalPurchaseYTD -4206.2024' }, {
    BusinessEntityID: '483',
    Demographics: 'TotalPurchaseYTD -3996.7435',
  }, { changedColumns: ['Demographics'], parentRowIds: ['rc-1'] }),
]
const DM2_NET: NetChange[] = [
  net('Person.Person', PERSON_KEY, 'UPDATED', { BusinessEntityID: '483', Demographics: 'TotalPurchaseYTD -4416.2024' }, {
    BusinessEntityID: '483',
    Demographics: 'TotalPurchaseYTD -3996.7435',
  }, ['Demographics'], ['f-16', 'f-23']),
  net('Production.TransactionHistory', pk(['TransactionID'], ['213443']), 'INSERTED', null, TH_ROW, [], ['f-13']),
  net('Sales.SalesOrderDetail', SOD_KEY, 'UPDATED', { SalesOrderID: '43663', SalesOrderDetailID: '52', OrderQty: '1', LineTotal: '419.458900' }, {
    SalesOrderID: '43663',
    SalesOrderDetailID: '52',
    OrderQty: '2',
    LineTotal: '838.917800',
  }, ['OrderQty', 'LineTotal'], ['f-24']),
  net('Sales.SalesOrderHeader', SOH_KEY, 'UPDATED', { SalesOrderID: '43663', RevisionNumber: '10', SubTotal: '419.4589', TotalDue: '472.3108' }, {
    SalesOrderID: '43663',
    RevisionNumber: '11',
    SubTotal: '838.9178',
    TotalDue: '891.7697',
  }, ['RevisionNumber', 'SubTotal', 'TotalDue'], ['f-17', 'f-20']),
]
// 적용(로그 20~21줄): committed · netSummaryMatch · volatileIgnored 3 · identityPairs 1 · 소요.
const DM2_APPLY = committed(
  [{ ...DM2_STMT, captureSeqRange: null }],
  DM2_SUM,
  {
    volatileIgnored: [
      'Production.TransactionHistory.ModifiedDate',
      'Production.TransactionHistory.TransactionDate',
      'Production.TransactionHistory.TransactionID',
    ],
    identityPairs: [{ table: 'Production.TransactionHistory', previewKey: pk(['TransactionID'], ['213443']), actualKey: pk(['TransactionID'], ['213443']) }],
  },
  { snapshot: 398, drift: 6, execute: 175, netDiff: 2144, commit: 6 },
)

// ---- DM3: DELETE Employee 1 → trigger_blocked (로그 22~24줄) ----
// 로그의 그래프 노드는 3개지만 둘은 종류가 비어 있다. 목업 v6처럼 문장·오류 2개와 BLOCKED 간선만 둔다.

const DM3_SQL = 'DELETE FROM HumanResources.Employee WHERE BusinessEntityID = 1;'
const DM3_A = analyzed(1, 'DELETE', 'HumanResources.Employee', DM3_SQL)
const DM3_ERR = { errorNumber: 3609, message: 'The transaction ended in the trigger. The batch has been aborted.', procedure: null, isToolFault: false }
const DM3_STMT = stmtResult(DM3_A, 'trigger_blocked', null, null, DM3_ERR)
edgeSeq = 0
const DM3_GRAPH: GraphDto = {
  nodes: [stNode(DM3_STMT, DM3_SQL), { type: 'error', id: 'err-1', errorNumber: 3609, message: DM3_ERR.message, isToolFault: false }],
  edges: [edge('st-1', 'err-1', 'BLOCKED', 'certain')],
}

// ---- DM6: CascadeLab 5만 행 DELETE → truncated (실측 로그 없음, 전부 예시값) ----
// README "어떻게 평가하나"(약 43.4초)·demo.http 124줄(SQL·imageCap 100000)을 따랐다. 연쇄 자식 건수는 근거가 없어 뺐다.

const DM6_SQL = 'DELETE FROM cl20.Parent WHERE id <= 50000;'
const DM6_A = analyzed(1, 'DELETE', 'cl20.Parent', DM6_SQL)
const DM6_F = [firing('f-1', 1, 1, 'cl20.Parent', 'DELETE', 50000, direct(), 'KEYS')]
const DM6_STMT = stmtResult(DM6_A, 'ok', 50000, [1, 1])
edgeSeq = 0
const DM6_GRAPH: GraphDto = { nodes: [stNode(DM6_STMT, DM6_SQL), fNode(DM6_F[0])], edges: [edge('st-1', 'f-1', 'DIRECT', 'certain')] }
const CL_SCHEMA: Omit<CatalogSchema, 'baselineAt'> = {
  database: 'CascadeLab',
  manifestVersion: 'm-cl-example',
  tables: [
    tbl('cl10', 'Region', ['id'], [col('id', 'int'), col('name', 'nvarchar(50)')]),
    tbl('cl20', 'Parent', ['id'], [col('id', 'int'), col('payload', 'nvarchar(100)', { nullable: true })]),
  ],
}
const parentKey = (i: number) => pk(['id'], [String(i + 1)])

// ---- DM7: UPDATE Product 680 ListPrice+1.00 → 적용 시 drift_detected (로그 26~29줄) ----

const DM7_SQL = 'UPDATE Production.Product SET ListPrice = ListPrice + 1.00 WHERE ProductID = 680;'
const DM7_A = analyzed(1, 'UPDATE', 'Production.Product', DM7_SQL)
const DM7_F = [firing('f-1', 1, 1, 'Production.Product', 'UPDATE', 1, direct())]
const DM7_STMT = stmtResult(DM7_A, 'ok', 1, [1, 1])
edgeSeq = 0
const DM7_GRAPH: GraphDto = { nodes: [stNode(DM7_STMT, DM7_SQL), fNode(DM7_F[0])], edges: [edge('st-1', 'f-1', 'DIRECT', 'certain')] }
// driftRows expected = 로그 29줄 그대로. 이후 값 1432.5000 = expected + 1.00 계산.
const PRODUCT_680: Values = {
  ProductID: '680',
  Name: 'HL Road Frame - Black, 58',
  ProductNumber: 'FR-R92B-58',
  MakeFlag: 'true',
  FinishedGoodsFlag: 'true',
  Color: 'Black',
  SafetyStockLevel: '500',
  ReorderPoint: '375',
  StandardCost: '1059.3100',
  ListPrice: '1431.5000',
  Size: '58',
  SizeUnitMeasureCode: 'CM ',
  WeightUnitMeasureCode: 'LB ',
  Weight: '2.24',
  DaysToManufacture: '1',
  ProductLine: 'R ',
  Class: 'H ',
  Style: 'U ',
  ProductSubcategoryID: '14',
  ProductModelID: '6',
  SellStartDate: '2019-04-30T00:00:00',
  SellEndDate: null,
  DiscontinuedDate: null,
  rowguid: '43DD68D6-14A4-461F-9069-55309D90EA7E',
  ModifiedDate: '2025-02-07T10:01:36.827',
}
const PRODUCT_KEY = pk(['ProductID'], ['680'])
const PRODUCT_AFTER: Values = { ...PRODUCT_680, ListPrice: '1432.5000' }
const DM7_APPLY = (finishedAt: string, previewId: string): ApplyResult => ({
  previewId,
  status: 'drift_detected',
  finishedAt,
  preconditionFailures: [],
  verification: verification({
    driftRows: [
      {
        table: 'Production.Product',
        key: PRODUCT_KEY,
        kind: 'CHANGED',
        columns: ['ListPrice'],
        expected: PRODUCT_680,
        actual: { ...PRODUCT_680, ListPrice: '1431.5100' },
        expectedMultiplicity: null,
        actualMultiplicity: null,
      },
    ],
  }),
  statements: [{ ...DM7_STMT, status: 'not_run', rowsAffected: null, captureSeqRange: null }], // 예시값(로그에 없음)
  message: '미리보기 뒤 원본의 before 행 1건이 바뀌어 실행하지 않고 롤백함. 샌드박스를 새로 고친 뒤 다시 미리보기 할 것',
  sandboxRefreshStarted: false,
  timingsMs: { snapshot: 413, drift: 5, execute: 0, netDiff: 0, commit: 0 },
})

// ---- 거부(422): DDL 섞인 일괄 ----
// 규칙 ID·메시지·설계 참조는 BatchAnalyzer(AnalysisRules.RejectDdl, E10) 그대로. DROP TABLE Sales.Store 문장은 목업 v4 예시값.

const REJ_DROP = 'DROP TABLE Sales.Store;'
const REJ_SQL = `${DM1_SQL}\n${REJ_DROP}`
const REJ_SPAN: SourceSpan = { startLine: 2, startColumn: 1, endLine: 2, endColumn: 11 }
const REJ_DIAG = {
  statementIndex: 2,
  span: REJ_SPAN,
  severity: 'error' as const,
  ruleId: 'REJECT_DDL',
  message: 'DDL은 미리보기할 수 없습니다. 스키마를 바꾸면 트랜잭션 제약과 DDL 트리거 반응이 달라집니다.',
  docRef: 'E10',
}

// ---- 목록 ----

const SRC_NONE = '실측 로그 없음'

type BaseId = 'dm1' | 'dm2' | 'dm3' | 'dm6' | 'dm7' | 'dm2c' | 'rej' | 'building'

const BASE: Record<BaseId, Fixture> = {
  dm1: {
    id: 'dm1',
    label: 'DM1 정상 · 삭제 4행',
    source: 'e2e 로그 6줄(묶음 5·netSummary·경고 0)·47줄(트리거 3개 관측 여부)',
    examples: '묶음 id·captureSeq·단계별 소요·환경 지문 상세·CustomerID 외 열 값 · 적용 결과(committed)는 예시값',
    sql: DM1_SQL,
    imageCap: null,
    autoRun: true,
    autoApply: false,
    sandbox: READY,
    schema: AW_SCHEMA,
    analysis: accepted(DM1_A),
    preview: previewOf({
      previewId: 'pv_20261112_f9fbd6e4',
      status: 'ok',
      sqlHash: 'sha256:dbaf73cd4ffd(예시값: 로그는 앞 12자만)',
      statements: [DM1_STMT],
      firings: DM1_F,
      netSummary: DM1_SUM,
      graph: DM1_GRAPH,
      totalMs: 1581,
    }),
    eventRows: DM1_EVENT,
    netRows: DM1_NET,
    apply: committed([{ ...DM1_STMT, captureSeqRange: null }], DM1_SUM, {}, { snapshot: 400, drift: 5, execute: 160, netDiff: 1500, commit: 6 }),
  },
  dm2: {
    id: 'dm2',
    label: 'DM2 수정 · 추가 1 · 수정 3',
    source: 'e2e 로그 16~19줄(묶음 12·netSummary·net 4행·volatile) · sql-dm2-after.log(이전→이후)',
    examples: '깊이 3 묶음의 부모·트리거 배치, 같은 행을 나눈 중간값, TransactionDate·ModifiedDate 값은 예시값',
    sql: DM2_SQL,
    imageCap: null,
    autoRun: true,
    autoApply: false,
    sandbox: READY,
    schema: AW_SCHEMA,
    analysis: accepted(DM2_A),
    preview: previewOf({
      previewId: 'pv_20261112_dc819fb8',
      status: 'ok',
      sqlHash: 'sha256:a0dadfacb20e(예시값: 로그는 앞 12자만)',
      statements: [DM2_STMT],
      firings: DM2_F,
      netSummary: DM2_SUM,
      graph: DM2_GRAPH,
      totalMs: 1234,
    }),
    eventRows: DM2_EVENT,
    netRows: DM2_NET,
    apply: DM2_APPLY,
  },
  dm3: {
    id: 'dm3',
    label: 'DM3 트리거 거부 · trigger_blocked',
    source: 'e2e 로그 22~24줄(오류 3609·status trigger_blocked·적용 시 precondition_failed PREVIEW_NOT_OK)',
    examples: '그래프는 문장·오류 2노드(로그 3노드 중 종류 빈 것 생략) · 단계별 소요는 예시값',
    sql: DM3_SQL,
    imageCap: null,
    autoRun: true,
    autoApply: false,
    sandbox: READY,
    schema: AW_SCHEMA,
    analysis: accepted(DM3_A),
    preview: previewOf({
      previewId: 'pv_20261112_d3649f65',
      status: 'trigger_blocked',
      sqlHash: 'sha256:3cd2b6a1ec5e(예시값: 로그는 앞 12자만)',
      statements: [DM3_STMT],
      firings: [],
      netSummary: [],
      graph: DM3_GRAPH,
      totalMs: 2545,
    }),
    eventRows: [],
    netRows: [],
    apply: precondition('미리보기 상태가 ok가 아님.'),
  },
  dm6: {
    id: 'dm6',
    label: 'DM6 결과 잘림 · truncated(예시값)',
    source: SRC_NONE,
    examples: '전부 예시값: 5만 행 DELETE·키만 수집·약 43.4초(README), SQL·상한 100000(demo.http 124줄), 키 1~50000 생성',
    sql: DM6_SQL,
    imageCap: 100000,
    autoRun: true,
    autoApply: false,
    sandbox: { ...READY, manifest: { coveredTables: 2, excludedTables: 0, auditNotLast: 0 } },
    schema: CL_SCHEMA,
    analysis: accepted(DM6_A),
    preview: previewOf({
      previewId: 'pv_example_dm6',
      status: 'truncated',
      sqlHash: 'sha256:example-dm6',
      statements: [DM6_STMT],
      firings: DM6_F,
      netSummary: [sum('cl20.Parent', 0, 0, 50000)],
      graph: DM6_GRAPH,
      totalMs: 43400,
      capture: { imageCount: 100000, cap: 100000, truncated: true, manifestVersion: 'm-cl-example' },
      originDb: 'CascadeLab',
    }),
    eventRows: { firingIds: ['f-1'], total: 50000, make: (i) => rowChange(`rc-${i + 1}`, 'f-1', 'DELETE', parentKey(i), null, null) },
    netRows: { firingIds: ['f-1'], total: 50000, make: (i) => net('cl20.Parent', parentKey(i), 'DELETED', null, null, [], ['f-1']) },
    apply: precondition('미리보기 상태가 ok가 아님.'),
  },
  dm7: {
    id: 'dm7',
    label: 'DM7 드리프트 · 적용 결과',
    source: 'e2e 로그 26~29줄(원본 ListPrice +0.01 → drift_detected, driftRows expected 1431.5000·actual 1431.5100)',
    examples: '적용 결과의 statements(not_run)·단계별 소요는 예시값 · 이후 값 1432.5000은 계산',
    sql: DM7_SQL,
    imageCap: null,
    autoRun: true,
    autoApply: true,
    sandbox: READY,
    schema: AW_SCHEMA,
    analysis: accepted(DM7_A),
    preview: previewOf({
      previewId: 'pv_20261112_0ab44477',
      status: 'ok',
      sqlHash: 'sha256:1606a9a1c51c(예시값: 로그는 앞 12자만)',
      statements: [DM7_STMT],
      firings: DM7_F,
      netSummary: [sum('Production.Product', 0, 1, 0)],
      graph: DM7_GRAPH,
      totalMs: 202,
    }),
    eventRows: [rowChange('rc-1', 'f-1', 'UPDATE', PRODUCT_KEY, PRODUCT_680, PRODUCT_AFTER, { changedColumns: ['ListPrice'] })],
    netRows: [net('Production.Product', PRODUCT_KEY, 'UPDATED', PRODUCT_680, PRODUCT_AFTER, ['ListPrice'], ['f-1'])],
    apply: DM7_APPLY,
  },
  dm2c: {
    id: 'dm2c',
    label: 'DM2 적용 완료 · committed',
    source: 'e2e 로그 20~21줄(committed·netSummaryMatch·identityPairs 1·volatileIgnored·sandboxRefreshStarted)',
    examples: 'DM2와 같음 · 적용 뒤 샌드박스 새로 고침(building 8초)은 흉내',
    sql: DM2_SQL,
    imageCap: null,
    autoRun: true,
    autoApply: true,
    sandbox: READY,
    schema: AW_SCHEMA,
    analysis: accepted(DM2_A),
    preview: previewOf({
      previewId: 'pv_20261112_dc819fb8',
      status: 'ok',
      sqlHash: 'sha256:a0dadfacb20e(예시값: 로그는 앞 12자만)',
      statements: [DM2_STMT],
      firings: DM2_F,
      netSummary: DM2_SUM,
      graph: DM2_GRAPH,
      totalMs: 1234,
    }),
    eventRows: DM2_EVENT,
    netRows: DM2_NET,
    apply: DM2_APPLY,
  },
  rej: {
    id: 'rej',
    label: '422 거부 · DROP TABLE',
    source: '422 ProblemDetails.rejects 모양(types.ts) · 규칙 REJECT_DDL·문구는 BatchAnalyzer',
    examples: 'DROP TABLE Sales.Store 문장은 목업 예시값 · 분석이 거부해도 422 화면을 보이려고 미리보기를 한 번 보낸다',
    sql: REJ_SQL,
    imageCap: null,
    autoRun: true,
    autoApply: false,
    sandbox: READY,
    schema: AW_SCHEMA,
    analysis: { statements: [DM1_A], rejects: [REJ_DIAG], warnings: [], diagnostics: [REJ_DIAG], isAccepted: false },
    preview: {
      type: 'https://example.local/problems/rejected',
      title: '미리보기할 수 없는 문장이 있습니다.',
      status: 422,
      rejects: [{ statementIndex: 2, ruleId: 'REJECT_DDL', span: REJ_SPAN }],
    },
    eventRows: [],
    netRows: [],
    apply: null,
  },
  building: {
    id: 'building',
    label: '샌드박스 기준선 만드는 중 · 409',
    source: '409 reason BUILDING 모양(types.ts·client.ts 문구)',
    examples: '상태만 흉내(기준선 없음) · 409 화면을 보이려고 미리보기를 한 번 보낸다',
    sql: DM1_SQL,
    imageCap: null,
    autoRun: true,
    autoApply: false,
    sandbox: { state: 'building', baselineAt: null, fingerprintMatch: true, locked: false, manifest: null },
    schema: null,
    analysis: accepted(DM1_A),
    preview: { type: 'https://example.local/problems/conflict', title: '기준선을 만드는 중입니다.', status: 409, reason: 'BUILDING' },
    eventRows: [],
    netRows: [],
    apply: null,
  },
}

// ---- N4 시험 데이터: dm1(삭제 4행)을 바탕으로 상태 필드만 바꾼다. 서버 이름·로그인·지문 문구는 모두 예시값이다. ----

const N4_SOURCE = 'N4 계약(contract-n4.md §4~6)의 필드 모양 · types.ts'
const N4_EXAMPLES = '서버·DB·로그인 이름, 지문 경고 문구, 한계 문구는 예시값(백엔드 문구와 다를 수 있음)'
const DM1 = BASE.dm1

const SPLIT_SANDBOX: SandboxStatus = {
  ...READY,
  mode: 'split',
  origin: { server: 'devsql01', database: 'AW_Origin', login: 'dbr_app', isSysadmin: false },
  fingerprintWarnings: [],
  fingerprintMismatches: [],
  baselineStale: false,
  originReachable: true,
}

/** dm1 적용 결과를 분리 모드 커밋으로 바꾼다: 샌드박스 새로 고침 안 함 + 실행 주체 불일치 한계(예시값). */
function splitCommitted(finishedAt: string, previewId: string, req: ApplyRequest): ApplyResult {
  const r = DM1.apply!(finishedAt, previewId, req)
  return {
    ...r,
    sandboxRefreshStarted: false,
    verification: {
      ...r.verification,
      limitations: ['미리보기는 샌드박스 로그인으로, 적용은 원본 로그인(dbr_app)으로 실행했습니다. 두 로그인의 권한·기본값이 다르면 결과가 다를 수 있습니다.'],
    },
  }
}

/** 원본 사전 경고 문구(ApplyResult.verification.limitations에 섞여 온다. 막지 않는다). 문구는 예시값(백엔드 문구와 다를 수 있음). */
const ORIGIN_PRECHECK_WARNINGS = [
  '원본 DB에 TRUSTWORTHY가 켜져 있습니다. 같은 SQL을 높은 권한 문맥에서 실행할 수 있습니다.',
  '원본 테이블이 복제 게시에 들어 있습니다. 적용한 변경이 구독자에게 전파됩니다.',
  '원본 DB에 CDC(변경 데이터 캡처)가 켜져 있습니다. 적용한 변경이 CDC 테이블에 남습니다.',
  '원본 DB에 Service Broker가 켜져 있습니다. 트리거가 큐에 메시지를 보냈다면 롤백되지 않을 수 있습니다.',
]

/** 동의 없이는 WEAK_VERIFICATION_NOT_ACCEPTED, 동의하면 KEYSET_HASH 검증으로 커밋. */
function weakApply(finishedAt: string, previewId: string, req: ApplyRequest): ApplyResult {
  if (!req.allowKeysetVerification) {
    return {
      ...precondition('원본에 스냅샷 권한이 없어 약한 검증(KEYSET_HASH)이 필요합니다. 동의 없이는 적용하지 않았습니다.')(finishedAt, previewId),
      preconditionFailures: ['WEAK_VERIFICATION_NOT_ACCEPTED'],
    }
  }
  const r = DM1.apply!(finishedAt, previewId, req)
  return {
    ...r,
    sandboxRefreshStarted: false,
    verification: {
      ...r.verification,
      mode: 'KEYSET_HASH',
      limitations: [
        'KEYSET_HASH 검증: 미리보기에 없던 테이블은 행 수와 CHECKSUM_AGG만 비교합니다. 해시 충돌이나 비교할 수 없는 형식의 열 변경은 놓칠 수 있습니다.',
        ...ORIGIN_PRECHECK_WARNINGS,
      ],
    },
  }
}

/** KEYSET_HASH 검증이 미리보기에 없던 테이블의 변화를 찾아 롤백한다(OUTSIDE_TABLE_CHANGED). 예시값. */
function outsideApply(finishedAt: string, previewId: string, req: ApplyRequest): ApplyResult {
  // 결과 화면을 바로 보이려고 동의 확인 단계는 건너뛴다(동의 흐름은 weak 시험 데이터에서 본다).
  const r = weakApply(finishedAt, previewId, { ...req, allowKeysetVerification: true })
  return {
    ...r,
    status: 'rolled_back_mismatch',
    message: '미리보기에 없던 테이블이 바뀌어 롤백함. 원본은 바뀌지 않았습니다.',
    sandboxRefreshStarted: false,
    verification: {
      ...r.verification,
      mismatches: [
        {
          kind: 'OUTSIDE_TABLE_CHANGED',
          statementIndex: null,
          table: 'Production.ProductListPriceHistory',
          key: null,
          column: null,
          expected: 'count=395,checksum=1203491',
          actual: 'count=396,checksum=-88231004',
          message: '미리보기에 없던 테이블의 행 수·체크섬이 실행 전후로 달라졌습니다(트리거나 원본에만 있는 객체가 쓴 것으로 보입니다).',
        },
      ],
    },
  }
}

// ---- EXEC(프로시저 호출) 시험 데이터: dm1(삭제)을 바탕으로 문장·그래프만 바꾼다. 미리보기 전용(적용 막힘). ----
// 계약 contract-exec.md §5(EXEC 미리보기)의 모양. 프로시저 이름·본문 동작·경고 문구는 전부 예시값이다(백엔드 문구와 다를 수 있음).
const EXEC_SOURCE = 'EXEC 계약(contract-exec.md · be-exec 워크트리 Contracts: AnalyzedStatement·StatementResult·procedureCall 노드·PROC_DIRECT)'
const EXEC_EXAMPLES = '프로시저·매개변수·본문 동작·경고 문구·행 값·소요 시간은 예시값(백엔드 문구와 다를 수 있음)'

const EXEC_PROC = 'Sales.uspCancelOrder'
const EXEC_SQL = 'EXEC Sales.uspCancelOrder @SalesOrderID = 43697;'
const execAnalyzed = (sql: string, procedure: string, parameters: string[]): AnalyzedStatement => ({
  index: 1,
  batch: 1,
  position: 1,
  kind: 'EXEC',
  target: null,
  procedure,
  parameters,
  span: line1(sql),
  sql,
  nondeterministicColumns: [],
})
const EXEC_A = execAnalyzed(EXEC_SQL, EXEC_PROC, ['@SalesOrderID = 43697'])

function execResult(
  a: AnalyzedStatement,
  status: StatementResult['status'],
  returnValue: number | null,
  resultSetCount: number | null,
  error: StatementResult['error'] = null,
): StatementResult {
  const { index, batch, position, span } = a
  return {
    index,
    batch,
    position,
    kind: 'EXEC',
    target: null,
    span,
    status,
    rowsAffected: null, // EXEC는 늘 null(계약)
    returnValue,
    resultSetCount,
    captureSeqRange: status === 'ok' ? [1, 4] : null,
    error,
  }
}

function procNode(a: AnalyzedStatement, s: StatementResult, o: Partial<Extract<GraphNode, { type: 'procedureCall' }>> = {}): GraphNode {
  return {
    type: 'procedureCall',
    id: `st-${s.index}`,
    statementIndex: s.index,
    label: `EXEC ${a.procedure}`,
    procedure: a.procedure ?? '',
    status: s.status,
    returnValue: s.returnValue ?? null,
    resultSetCount: s.resultSetCount ?? null,
    expandedCalls: [],
    writeTargets: [],
    unobservedWriteTargets: [],
    ...o,
  }
}

const EXEC_WARNINGS: Diagnostic[] = [
  {
    statementIndex: 1,
    span: line1(EXEC_SQL),
    severity: 'warning',
    ruleId: 'WARN_PROC_RESULT_SET',
    message: '프로시저가 결과 집합을 냅니다. 개수만 세고 내용은 버립니다.',
    docRef: null,
  },
  {
    statementIndex: 1,
    span: line1(EXEC_SQL),
    severity: 'warning',
    ruleId: 'WARN_PROC_CATCH_SWALLOWS',
    message: 'dbo.uspLogError 호출 경로의 TRY/CATCH가 오류를 삼킵니다(CATCH에 THROW·RAISERROR 없음). 실패해도 문장은 성공으로 끝날 수 있습니다.',
    docRef: null,
  },
]

// 프로시저 본문(예시값): 사유 행 삭제 → 주문 헤더 삭제(FK로 상세 연쇄) → 실패하면 dbo.uspLogError가 dbo.ErrorLog에 기록(이번엔 안 탐 = 관측 안 됨)
const EXEC_F = [
  firing('f-1', 1, 1, 'Sales.SalesOrderHeaderSalesReason', 'DELETE', 2, procDirect()),
  firing('f-2', 2, 1, 'Sales.SalesOrderHeader', 'DELETE', 1, procDirect()),
  firing('f-3', 3, 2, 'Sales.SalesOrderDetail', 'DELETE', 1, fkCascade('f-2', FK_SOD, 'Sales.SalesOrderHeader', 'Sales.SalesOrderDetail', 1)),
]
const EXEC_STMT = execResult(EXEC_A, 'ok', 0, 1)
edgeSeq = 100
const EXEC_GRAPH: GraphDto = {
  nodes: [
    procNode(EXEC_A, EXEC_STMT, {
      expandedCalls: ['dbo.uspLogError'],
      writeTargets: ['Sales.SalesOrderHeaderSalesReason', 'Sales.SalesOrderHeader', 'dbo.ErrorLog'],
      unobservedWriteTargets: ['dbo.ErrorLog'],
    }),
    ...EXEC_F.map(fNode),
  ],
  edges: [
    edge('st-1', 'f-1', 'PROC_DIRECT', 'certain'),
    edge('st-1', 'f-2', 'PROC_DIRECT', 'certain'),
    edge('f-2', 'f-3', 'FK', 'certain', { name: FK_SOD, matched: 1 }),
  ],
}
const EXEC_SUM = [sum('Sales.SalesOrderDetail', 0, 0, 1), sum('Sales.SalesOrderHeader', 0, 0, 1), sum('Sales.SalesOrderHeaderSalesReason', 0, 0, 2)]
const EXEC_EVENT: RowChange[] = [
  rowChange('rc-1', 'f-1', 'DELETE', pk(['SalesOrderID', 'SalesReasonID'], ['43697', '5']), { SalesOrderID: '43697', SalesReasonID: '5' }, null),
  rowChange('rc-2', 'f-1', 'DELETE', pk(['SalesOrderID', 'SalesReasonID'], ['43697', '9']), { SalesOrderID: '43697', SalesReasonID: '9' }, null),
  rowChange('rc-3', 'f-2', 'DELETE', pk(['SalesOrderID'], ['43697']), { SalesOrderID: '43697', CustomerID: '21768', Status: '5' }, null),
  rowChange('rc-4', 'f-3', 'DELETE', pk(['SalesOrderID', 'SalesOrderDetailID'], ['43697', '353']), { SalesOrderID: '43697', SalesOrderDetailID: '353' }, null, {
    parentRowIds: ['rc-3'],
  }),
]
const EXEC_NET: NetChange[] = EXEC_EVENT.map((r) => net(EXEC_F.find((f) => f.id === r.firingId)!.table, r.key, 'DELETED', r.before, null, [], [r.firingId]))

/** EXEC는 [적용]이 막힌다. 서버가 PROC_APPLY_NOT_SUPPORTED로 돌려주는 응답(화면은 미리 막아 보통 도달하지 않는다). */
function procApplyBlocked(finishedAt: string, previewId: string): ApplyResult {
  return { ...precondition('프로시저 호출(EXEC)은 아직 적용할 수 없습니다(미리보기 전용).')(finishedAt, previewId), preconditionFailures: ['PROC_APPLY_NOT_SUPPORTED'] }
}

// 거부: 동적 SQL을 쓰는 프로시저(REJECT_PROC_DYNAMIC_SQL)
const REJEXEC_SQL = "EXEC dbo.uspRunReport @TableName = N'Sales.Store';"
const REJEXEC_A = execAnalyzed(REJEXEC_SQL, 'dbo.uspRunReport', ["@TableName = N'Sales.Store'"])
const REJEXEC_DIAG: Diagnostic = {
  statementIndex: 1,
  span: line1(REJEXEC_SQL),
  severity: 'error',
  ruleId: 'REJECT_PROC_DYNAMIC_SQL',
  message: '프로시저 본문(dbo.uspRunReport)에 동적 SQL(EXEC 문자열)이 있어 어떤 테이블에 쓸지 알 수 없습니다. 미리보기할 수 없습니다.',
  docRef: null,
}

// 실패: 프로시저가 도구 트랜잭션을 ROLLBACK(계약: failed · isToolFault=false · 오류 번호 0 · 메시지 "프로시저가 트랜잭션을 롤백함")
const RB_SQL = 'EXEC Sales.uspCancelOrderStrict @SalesOrderID = 43697;'
const RB_A = execAnalyzed(RB_SQL, 'Sales.uspCancelOrderStrict', ['@SalesOrderID = 43697'])
const RB_ERR = { errorNumber: 0, message: '프로시저가 트랜잭션을 롤백함', procedure: null, isToolFault: false }
const RB_STMT = execResult(RB_A, 'failed', null, null, RB_ERR)
const RB_WARN: Diagnostic = {
  statementIndex: 1,
  span: line1(RB_SQL),
  severity: 'warning',
  ruleId: 'WARN_PROC_ROLLBACK',
  message: '프로시저가 ROLLBACK을 합니다. 실행되면 미리보기가 실패로 끝납니다("프로시저가 트랜잭션을 롤백함").',
  docRef: null,
}
edgeSeq = 200
const RB_GRAPH: GraphDto = {
  nodes: [
    procNode(RB_A, RB_STMT, { writeTargets: ['Sales.SalesOrderHeader', 'Sales.SalesOrderDetail'], unobservedWriteTargets: ['Sales.SalesOrderHeader', 'Sales.SalesOrderDetail'] }),
    { type: 'error', id: 'err-1', errorNumber: 0, message: RB_ERR.message, isToolFault: false },
  ],
  edges: [edge('st-1', 'err-1', 'BLOCKED', 'certain')],
}

function variant(id: FixtureId, label: string, over: Partial<Fixture>): Fixture {
  return { ...DM1, id, label, source: N4_SOURCE, examples: N4_EXAMPLES, ...over }
}

const SETTINGS_PROFILE: SettingsView = {
  source: 'profile',
  configured: true,
  mode: 'split',
  origin: { server: 'devsql01', database: 'AW_Origin', auth: 'sql', user: 'dbr_app', hasStoredPassword: true, trustServerCertificate: false },
  sandbox: { server: '127.0.0.1,14333', database: 'AW_Sandbox', auth: 'sql', user: 'sa', hasStoredPassword: true },
  backup: { serverPath: 'D:\\Backup', sharePath: '\\\\devsql01\\dbr', importFile: null },
}
/** source:none 처음 응답: 서버가 채워 보내는 기본값(be-pack-1 답) */
const SETTINGS_NONE: SettingsView = {
  source: 'none',
  configured: false,
  mode: 'split',
  origin: { server: '', database: 'AW_Origin', auth: 'windows', user: null, hasStoredPassword: false, trustServerCertificate: false },
  sandbox: { server: '127.0.0.1,14333', database: 'AW_Sandbox', auth: 'sql', user: 'sa', hasStoredPassword: false },
  backup: { serverPath: null, sharePath: null, importFile: null },
}

export const FIXTURES: Record<FixtureId, Fixture> = {
  ...BASE,
  split: variant('split', '분리 모드 · 원본 표시 · 지문 경고 1', {
    examples: N4_EXAMPLES + ' · 적용은 분리 모드 커밋(샌드박스 새로 고침 시작 안 함)',
    sandbox: {
      ...SPLIT_SANDBOX,
      fingerprintWarnings: ['서버 데이터 정렬이 다릅니다: 원본 SQL_Latin1_General_CP1_CI_AS · 샌드박스 Latin1_General_100_CI_AS'],
    },
    apply: splitCommitted,
  }),
  stale: variant('stale', '기준선 오래됨(분리 모드)', {
    sandbox: { ...SPLIT_SANDBOX, baselineStale: true },
    apply: splitCommitted,
  }),
  weak: variant('weak', '약한 검증 동의(KEYSET_HASH) · 인스턴스 경고 · 원본 사전 경고', {
    examples:
      N4_EXAMPLES + ' · 적용: 동의 없으면 WEAK_VERIFICATION_NOT_ACCEPTED, 동의하면 KEYSET_HASH 커밋(한계 문구에 원본 사전 경고 4종) · 인스턴스 경고 문구는 예시값',
    sandbox: {
      ...SPLIT_SANDBOX,
      origin: { server: 'devsql01', database: 'AW_Origin', login: 'sa', isSysadmin: true },
      instanceWarnings: [
        '샌드박스 인스턴스에 xp_cmdshell이 켜져 있습니다. 미리보기 SQL이 OS 명령을 실행할 수 있는 환경입니다.',
        '샌드박스 인스턴스에 연결된 서버(LinkedSrv01)가 있습니다. 미리보기 SQL이 다른 서버에 닿을 수 있습니다.',
      ],
    },
    apply: weakApply,
  }),
  outside: variant('outside', '약한 검증 불일치 롤백 · OUTSIDE_TABLE_CHANGED', {
    examples: N4_EXAMPLES + ' · 미리보기 뒤 자동으로 적용: 동의 확인 없이 rolled_back_mismatch(OUTSIDE_TABLE_CHANGED), 테이블·체크섬 값은 예시값',
    autoApply: true,
    sandbox: { ...SPLIT_SANDBOX, origin: { server: 'devsql01', database: 'AW_Origin', login: 'dbr_app', isSysadmin: false } },
    apply: outsideApply,
  }),
  unreachable: variant('unreachable', '원본 연결 안 됨 · 적용 막힘', {
    sandbox: { ...SPLIT_SANDBOX, originReachable: false },
  }),
  mismatch: variant('mismatch', '지문 불일치(차단 2 · 경고 1)', {
    sandbox: {
      ...SPLIT_SANDBOX,
      fingerprintMatch: false,
      fingerprintMismatches: ['호환성 수준이 다릅니다: 원본 160 · 샌드박스 150', 'RECURSIVE_TRIGGERS 설정이 다릅니다: 원본 OFF · 샌드박스 ON'],
      fingerprintWarnings: ['언어 설정이 다릅니다: 원본 한국어 · 샌드박스 us_english'],
    },
  }),
  first: variant('first', '첫 실행 · 설정 없음', {
    autoRun: false,
    settings: SETTINGS_NONE,
    sandbox: { state: 'error', baselineAt: null, fingerprintMatch: true, locked: false, manifest: null },
    sandboxProblem: { status: 503, title: 'SQL Server에 연결할 수 없습니다' },
  }),
  settings: variant('settings', '설정 · 저장된 프로필', { autoRun: false, settings: SETTINGS_PROFILE, sandbox: SPLIT_SANDBOX }),
  env: variant('env', '설정 · 환경 변수(읽기 전용)', {
    autoRun: false,
    settings: { ...SETTINGS_PROFILE, source: 'env' },
    sandbox: SPLIT_SANDBOX,
  }),
  nosession: variant('nosession', '401 NO_SESSION', {
    autoRun: false,
    sandboxProblem: { status: 401, title: '실행 파일이 연 브라우저 창에서만 쓸 수 있습니다', reason: 'NO_SESSION' },
  }),
  exec: variant('exec', 'EXEC 성공 · 프로시저 호출 → 직접 쓰기 2 → FK 연쇄 1 (미리보기 전용)', {
    source: EXEC_SOURCE,
    examples: EXEC_EXAMPLES + ' · [적용]은 막힘(PROC_APPLY_NOT_SUPPORTED)',
    sql: EXEC_SQL,
    analysis: { ...accepted(EXEC_A), warnings: EXEC_WARNINGS, diagnostics: EXEC_WARNINGS },
    preview: previewOf({
      previewId: 'pv_example_exec',
      status: 'ok',
      sqlHash: 'sha256:example-exec',
      statements: [EXEC_STMT],
      firings: EXEC_F,
      netSummary: EXEC_SUM,
      graph: EXEC_GRAPH,
      totalMs: 1420,
      warnings: EXEC_WARNINGS,
    }),
    eventRows: EXEC_EVENT,
    netRows: EXEC_NET,
    apply: procApplyBlocked,
  }),
  execrej: variant('execrej', 'EXEC 거부 · REJECT_PROC_DYNAMIC_SQL (422)', {
    source: EXEC_SOURCE,
    examples: EXEC_EXAMPLES + ' · 분석이 거부해도 422 화면을 보이려고 미리보기를 한 번 보낸다',
    sql: REJEXEC_SQL,
    analysis: { statements: [REJEXEC_A], rejects: [REJEXEC_DIAG], warnings: [], diagnostics: [REJEXEC_DIAG], isAccepted: false },
    preview: {
      type: 'https://example.local/problems/rejected',
      title: '미리보기할 수 없는 문장이 있습니다.',
      status: 422,
      rejects: [{ statementIndex: 1, ruleId: 'REJECT_PROC_DYNAMIC_SQL', span: REJEXEC_DIAG.span }],
    },
    eventRows: [],
    netRows: [],
    apply: null,
  }),
  execrb: variant('execrb', 'EXEC 실패 · 프로시저가 트랜잭션을 롤백함 (failed)', {
    source: EXEC_SOURCE,
    examples: EXEC_EXAMPLES + ' · 오류 번호 0은 StatementRunner.ProcedureRollbackError(원래 오류가 없을 때)를 따름',
    sql: RB_SQL,
    analysis: { ...accepted(RB_A), warnings: [RB_WARN], diagnostics: [RB_WARN] },
    preview: previewOf({
      previewId: 'pv_example_execrb',
      status: 'failed',
      sqlHash: 'sha256:example-execrb',
      statements: [RB_STMT],
      firings: [],
      netSummary: [],
      graph: RB_GRAPH,
      totalMs: 980,
      warnings: [RB_WARN],
    }),
    eventRows: [],
    netRows: [],
    apply: procApplyBlocked,
  }),
}
