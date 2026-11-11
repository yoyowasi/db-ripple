// 미리보기 작업 흐름: SQL 편집 → 분석 → 미리보기 → 적용·취소.
import { defineStore } from 'pinia'
import { computed, ref, shallowRef } from 'vue'
import {
  ApiError,
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
import { useSandboxStore } from './sandbox'

// AdventureWorks(공개 샘플) 주문 상세 수량 변경. 트리거 연쇄가 보이는 시연용 기본 문장이다.
export const SAMPLE_SQL = `UPDATE Sales.SalesOrderDetail
SET OrderQty = OrderQty + 1
WHERE SalesOrderID = 43659 AND SalesOrderDetailID = 1;
`

function toApiError(e: unknown): ApiError {
  return e instanceof ApiError ? e : new ApiError(0, null, String(e))
}

/** 첫 선택: 행이 있는 묶음 중 수집 순번이 가장 작은 것(0행 묶음은 E23에 따라 기본 숨김). */
function firstFiringId(p: PreviewResult): string | null {
  const f = [...p.firings].filter((x) => x.rowCount > 0).sort((a, b) => a.captureSeq - b.captureSeq)[0]
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

  // 적용·취소
  const applyResult = shallowRef<ApplyResult | null>(null)
  const applying = ref(false)
  const applyError = shallowRef<ApiError | null>(null)
  const cancelError = shallowRef<ApiError | null>(null)
  /** 서버에서 이미 지워졌다(커밋·취소·404). 행 페이지를 더 읽을 수 없다. */
  const consumed = ref(false)

  let analyzeSeq = 0
  let analyzeCtrl: AbortController | null = null
  let analyzeTimer: number | undefined
  let runCtrl: AbortController | null = null

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
    selectedFiringId.value = null
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

  async function applyNow(): Promise<void> {
    const p = preview.value
    if (!p || applying.value) return
    applying.value = true
    applyError.value = null
    try {
      // 편집기 현재 SQL의 해시를 보낸다. 서버가 미리보기의 sqlHash와 대조해 다르면 SQL_HASH_MISMATCH로 막는다.
      const r = await applyPreview(p.previewId, await sqlHash(sql.value))
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
    applyResult,
    applying,
    applyError,
    cancelError,
    consumed,
    setSql,
    analyzeNow,
    runPreview,
    cancel,
    applyNow,
  }
})
