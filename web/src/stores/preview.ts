// 미리보기 작업 흐름: SQL 편집 → 분석 → 미리보기 → 적용·취소.
import { defineStore } from 'pinia'
import { computed, ref, shallowRef } from 'vue'
import {
  ApiError,
  CLIENT_ERROR_STATUS,
  analyze,
  applyPreview,
  cancelPreview,
  createPreview,
  isAbortError,
} from '../api/client'
import type {
  AnalysisResult,
  ApplyResult,
  Diagnostic,
  PreviewResult,
  RowView,
} from '../api/types'
import { sqlHash } from '../lib/sqlHash'
import { displayOrder } from '../lib/summary'
import { useSandboxStore } from './sandbox'

// AdventureWorks(공개 샘플) 주문 상세 수량 변경. 트리거 연쇄가 보이는 시연용 기본 문장이다.
/** 결과 영역 탭 */
export type ResultTab = 'graph' | 'rows'

export const SAMPLE_SQL = `UPDATE Sales.SalesOrderDetail
SET OrderQty = OrderQty + 1
WHERE SalesOrderID = 43659 AND SalesOrderDetailID = 1;
`

function toApiError(e: unknown): ApiError {
  return e instanceof ApiError ? e : new ApiError(CLIENT_ERROR_STATUS, null, String(e))
}

/** 첫 선택: 행이 있는 묶음 중 표시 순번이 가장 앞선 것(그래프·발동 목록의 #1과 같게, 0행 묶음은 E23에 따라 기본 숨김). */
function firstFiringId(p: PreviewResult): string | null {
  const order = displayOrder(p.firings)
  const f = p.firings.filter((x) => x.rowCount > 0).sort((a, b) => (order.get(a.id) ?? 0) - (order.get(b.id) ?? 0))[0]
  return f?.id ?? null
}

export const usePreviewStore = defineStore('preview', () => {
  const sql = ref(SAMPLE_SQL)
  const imageCap = ref<number | null>(null)

  // 분석
  const analysis = shallowRef<AnalysisResult | null>(null)
  const analyzing = ref(false)
  const analyzeError = shallowRef<ApiError | null>(null)
  const rejectDiagnostics = shallowRef<Diagnostic[]>([])

  // 미리보기
  const preview = shallowRef<PreviewResult | null>(null)
  const previewSql = ref('')
  const running = ref(false)
  const runStartedAt = ref<number | null>(null)
  const runError = shallowRef<ApiError | null>(null)
  const runAborted = ref(false)

  // 화면 선택
  const selectedFiringId = ref<string | null>(null)
  const view = ref<RowView>('event')
  const showZeroRows = ref(false)
  /** 결과 영역에서 보이는 탭(전파 그래프 | 행 변경) */
  const resultTab = ref<ResultTab>('graph')
  /** 결과 영역 오른쪽 서랍(선택한 묶음 상세)이 열렸는가 */
  const drawerOpen = ref(false)
  /** 서랍을 여는 동작(selectFiring openDrawer: true)마다 1 늘어난다. 서랍은 이때만 제목으로 포커스를 옮긴다. */
  const drawerFocusTick = ref(0)
  /** 레일 표·탐색기에서 마우스를 올린 테이블 "스키마.테이블"(그래프가 그 노드를 강조한다). 없으면 null */
  const hoveredTable = ref<string | null>(null)

  // 적용·취소
  const applyResult = shallowRef<ApplyResult | null>(null)
  const applying = ref(false)
  const applyError = shallowRef<ApiError | null>(null)
  const cancelError = shallowRef<ApiError | null>(null)
  /** 서버에서 이미 지워졌다(커밋·취소·404). 행 페이지를 더 읽을 수 없다. */
  const consumed = ref(false)
  /** 서버가 WEAK_VERIFICATION_NOT_ACCEPTED를 돌려줬다: 확인 상자에서 약한 검증 동의를 받아 다시 요청해야 한다. */
  const needsWeakConsent = ref(false)

  let analyzeSeq = 0
  let analyzeCtrl: AbortController | null = null
  let analyzeTimer: number | undefined
  let runCtrl: AbortController | null = null

  /** 묶음 id → 화면 표시 순번(1부터). 그래프·발동 목록·서랍·레일이 같은 값을 쓴다(lib/summary displayOrder). */
  const firingOrder = computed(() => displayOrder(preview.value?.firings ?? []))

  const diagnostics = computed<Diagnostic[]>(() => analysis.value?.diagnostics ?? rejectDiagnostics.value)

  // ---- 분석(C1) ----

  function setSql(next: string): void {
    sql.value = next
    scheduleAnalyze()
  }

  function scheduleAnalyze(delay = 700): void {
    if (analyzeTimer !== undefined) window.clearTimeout(analyzeTimer)
    analyzeTimer = window.setTimeout(() => void analyzeNow(), delay)
  }

  async function analyzeNow(): Promise<void> {
    if (analyzeTimer !== undefined) window.clearTimeout(analyzeTimer)
    analyzeTimer = undefined
    if (applying.value) return // 적용 중에는 분석하지 않는다(잠금 경합 방지)
    const text = sql.value
    analyzeCtrl?.abort()
    if (!text.trim()) {
      analysis.value = null
      analyzeError.value = null
      analyzing.value = false
      return
    }
    analyzeCtrl = new AbortController()
    const mine = ++analyzeSeq
    analyzing.value = true
    try {
      const res = await analyze(text, analyzeCtrl.signal)
      if (mine !== analyzeSeq) return
      analysis.value = res
      rejectDiagnostics.value = []
      analyzeError.value = null
    } catch (e) {
      if (isAbortError(e) || mine !== analyzeSeq) return
      analyzeError.value = toApiError(e)
    } finally {
      if (mine === analyzeSeq) analyzing.value = false
    }
  }

  // ---- 미리보기(C10) ----

  function clearResult(): void {
    preview.value = null
    previewSql.value = ''
    applyResult.value = null
    applyError.value = null
    cancelError.value = null
    consumed.value = false
    needsWeakConsent.value = false
    selectedFiringId.value = null
    drawerOpen.value = false
  }

  // ---- 화면 선택(그래프·서랍·레일·행 변경이 공유) ----

  /** 묶음을 고른다. openDrawer를 주면 서랍도 함께 열거나 닫는다(id가 null이면 늘 닫는다). */
  function selectFiring(id: string | null, opts: { openDrawer?: boolean } = {}): void {
    selectedFiringId.value = id
    if (id === null) drawerOpen.value = false
    else if (opts.openDrawer !== undefined) {
      drawerOpen.value = opts.openDrawer
      if (opts.openDrawer) drawerFocusTick.value++
    }
  }

  function setResultTab(tab: ResultTab): void {
    resultTab.value = tab
  }

  function setDrawerOpen(open: boolean): void {
    drawerOpen.value = open && selectedFiringId.value !== null
  }

  function setHoveredTable(table: string | null): void {
    hoveredTable.value = table
  }

  async function runPreview(): Promise<void> {
    if (running.value || applying.value) return
    const text = sql.value
    if (!text.trim()) return
    clearResult()
    runError.value = null
    runAborted.value = false
    runCtrl = new AbortController()
    running.value = true
    runStartedAt.value = Date.now()
    try {
      const res = await createPreview(text, imageCap.value, runCtrl.signal)
      preview.value = res
      previewSql.value = text
      view.value = 'event'
      selectedFiringId.value = firstFiringId(res)
    } catch (e) {
      if (isAbortError(e)) {
        runAborted.value = true
      } else {
        const err = toApiError(e)
        runError.value = err
        if (err.status === 422) {
          // 422 본문에는 메시지가 없다. 분석 결과가 아직 없을 때만 규칙 ID로 줄 표시를 채운다.
          rejectDiagnostics.value = err.rejects.map((r) => ({
            statementIndex: r.statementIndex,
            span: r.span,
            severity: 'error' as const,
            ruleId: r.ruleId,
            message: '미리보기할 수 없는 문장입니다.',
            docRef: null,
          }))
        }
      }
    } finally {
      running.value = false
      runStartedAt.value = null
      runCtrl = null
      void useSandboxStore().poll()
    }
  }

  /** 진행 중이면 요청만 끊는다(서버는 끝까지 돌 수 있다). 끝난 미리보기면 서버에서 지운다([취소], 204). */
  async function cancel(): Promise<void> {
    if (running.value) {
      runCtrl?.abort()
      return
    }
    const p = preview.value
    if (!p) return
    cancelError.value = null
    try {
      await cancelPreview(p.previewId)
    } catch (e) {
      const err = toApiError(e)
      if (err.status !== 404) {
        cancelError.value = err
        return
      }
    }
    clearResult()
  }

  // ---- 적용(C15) ----

  /** allowKeyset: 사용자가 약한 검증(KEYSET)에 동의했다(확인 상자 체크). */
  async function applyNow(allowKeyset = false): Promise<void> {
    const p = preview.value
    if (!p || applying.value) return
    applying.value = true
    applyError.value = null
    needsWeakConsent.value = false
    try {
      // 편집기 현재 SQL의 해시를 보낸다. 서버가 미리보기의 sqlHash와 대조해 다르면 SQL_HASH_MISMATCH로 막는다.
      const r = await applyPreview(p.previewId, await sqlHash(sql.value), allowKeyset)
      // 동의만 없어서 실행하지 못한 경우: 결과 패널에 실패로 두지 않고 확인 상자가 동의를 받게 한다(미리보기는 그대로 유효).
      if (r.status === 'precondition_failed' && r.preconditionFailures.includes('WEAK_VERIFICATION_NOT_ACCEPTED')) {
        needsWeakConsent.value = true
        return
      }
      applyResult.value = r
      // Q36: 커밋되면 서버가 미리보기를 지우고 새로 고침을 시작한다.
      if (r.status === 'committed') consumed.value = true
    } catch (e) {
      const err = toApiError(e)
      applyError.value = err
      if (err.status === 404) consumed.value = true
    } finally {
      applying.value = false
      void useSandboxStore().poll()
    }
  }

  return {
    sql,
    imageCap,
    analysis,
    analyzing,
    analyzeError,
    diagnostics,
    preview,
    previewSql,
    running,
    runStartedAt,
    runError,
    runAborted,
    selectedFiringId,
    view,
    showZeroRows,
    resultTab,
    drawerOpen,
    drawerFocusTick,
    hoveredTable,
    setHoveredTable,
    firingOrder,
    applyResult,
    applying,
    applyError,
    cancelError,
    consumed,
    needsWeakConsent,
    setSql,
    analyzeNow,
    runPreview,
    cancel,
    applyNow,
    selectFiring,
    setResultTab,
    setDrawerOpen,
  }
})
