// GET /api/previews/{id}/rows 를 서버 페이지로 읽는다(offset·limit, limit 1..1000). 최종 차이 보기의 테이블 거르기도 서버가 한다(table=).
// 입력이 바뀌면 첫 페이지로 돌아가고, 늦게 도착한 응답은 버린다.
import { computed, ref, shallowRef, watch } from 'vue'
import type { Ref } from 'vue'
import { ApiError, CLIENT_ERROR_STATUS, getRows, isAbortError } from '../api/client'
import type { NetChange, RowChange, RowPage, RowView } from '../api/types'

export type AnyRowPage = RowPage<RowChange> | RowPage<NetChange>

export const EVENT_PAGE_SIZE = 100
export const NET_PAGE_SIZE = 1000

export function useRowPage(
  previewId: Ref<string | null>,
  view: Ref<RowView>,
  firingId: Ref<string | null>,
  /** view=net에서만 쓴다: "스키마.테이블". 서버가 그 테이블만 거르고 total도 거른 수로 준다. */
  table: Ref<string | null>,
  enabled: Ref<boolean>,
) {
  const page = shallowRef<AnyRowPage | null>(null)
  const loading = ref(false)
  const error = shallowRef<ApiError | null>(null)
  const offset = ref(0)
  // 발동 보기는 한 묶음의 행이라 100행, 최종 차이 보기는 한 테이블의 행이라 한 번에 더 읽는다.
  const limit = computed(() => (view.value === 'net' ? NET_PAGE_SIZE : EVENT_PAGE_SIZE))

  let seq = 0
  let ctrl: AbortController | null = null

  async function load(): Promise<void> {
    const id = previewId.value
    if (!id) {
      page.value = null
      error.value = null
      loading.value = false
      return
    }
    // 서버에서 지워진 뒤(적용·취소)에는 읽지 않고, 마지막으로 읽은 페이지를 그대로 둔다.
    if (!enabled.value) {
      loading.value = false
      return
    }
    ctrl?.abort()
    ctrl = new AbortController()
    const mine = ++seq
    loading.value = true
    error.value = null
    try {
      // firingId가 있으면 서버가 묶음으로 거른다(net은 explainedBy.firingIds에 포함된 행).
      const res = await getRows(
        id,
        { view: view.value, firingId: firingId.value, table: view.value === 'net' ? table.value : null, offset: offset.value, limit: limit.value },
        ctrl.signal,
      )
      if (mine !== seq) return
      page.value = res
    } catch (e) {
      if (isAbortError(e) || mine !== seq) return
      page.value = null
      error.value = e instanceof ApiError ? e : new ApiError(CLIENT_ERROR_STATUS, null, String(e))
    } finally {
      if (mine === seq) loading.value = false
    }
  }

  // 조건이 바뀌면 첫 페이지부터. 이미 0이면 바로 읽고, 아니면 offset 감시가 읽는다(중복 요청 방지).
  watch(
    [previewId, view, firingId, table, limit, enabled],
    () => {
      if (offset.value !== 0) offset.value = 0
      else void load()
    },
    { immediate: true },
  )
  watch(offset, () => void load())

  function next(): void {
    const p = page.value
    if (p && offset.value + limit.value < p.total) offset.value += limit.value
  }
  function prev(): void {
    offset.value = Math.max(0, offset.value - limit.value)
  }

  return { page, loading, error, offset, limit, next, prev, reload: load }
}
