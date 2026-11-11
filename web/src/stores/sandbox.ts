// 샌드박스 상태와 편집기 자동완성용 스키마. 스키마는 baselineAt을 키로 캐시한다(기준선이 바뀔 때만 다시 읽는다).
import { defineStore } from 'pinia'
import { computed, ref, shallowRef } from 'vue'
import { ApiError, describeApiError, getCatalogSchema, getSandbox, refreshSandbox } from '../api/client'
import type { CatalogSchema, SandboxStatus, SchemaTable } from '../api/types'

export const useSandboxStore = defineStore('sandbox', () => {
  const status = shallowRef<SandboxStatus | null>(null)
  const statusError = ref<string | null>(null)
  const schema = shallowRef<CatalogSchema | null>(null)
  const schemaError = ref<string | null>(null)
  const refreshing = ref(false)

  const schemaCache = new Map<string, CatalogSchema>()
  let timer: number | undefined
  let polling = false
  let schemaLoading = false

  const tableIndex = computed(() => {
    const m = new Map<string, SchemaTable>()
    for (const t of schema.value?.tables ?? []) m.set(t.table.toLowerCase(), t)
    return m
  })

  function findTable(name: string): SchemaTable | undefined {
    return tableIndex.value.get(name.toLowerCase())
  }

  async function loadSchema(baselineAt: string): Promise<void> {
    const cached = schemaCache.get(baselineAt)
    if (cached) {
      schema.value = cached
      schemaError.value = null
      return
    }
    if (schemaLoading) return
    schemaLoading = true
    try {
      const s = await getCatalogSchema()
      schemaCache.set(s.baselineAt, s)
      schema.value = s
      schemaError.value = null
    } catch (e) {
      // 409(기준선 없음·building 등)는 정상 흐름이다. 자동완성만 비어 있다.
      schemaError.value = e instanceof ApiError ? describeApiError(e) : String(e)
    } finally {
      schemaLoading = false
    }
  }

  async function poll(): Promise<void> {
    if (polling) return
    polling = true
    try {
      const s = await getSandbox()
      status.value = s
      statusError.value = null
      if (s.baselineAt && s.state !== 'building' && schema.value?.baselineAt !== s.baselineAt) {
        void loadSchema(s.baselineAt)
      }
    } catch (e) {
      statusError.value = e instanceof ApiError ? describeApiError(e) : String(e)
    } finally {
      polling = false
    }
  }

  function schedule(): void {
    const delay = status.value?.state === 'building' ? 2000 : 15000
    timer = window.setTimeout(async () => {
      await poll()
      if (timer !== undefined) schedule()
    }, delay)
  }

  function start(): void {
    if (timer !== undefined) return
    void poll()
    schedule()
  }

  function stop(): void {
    if (timer !== undefined) window.clearTimeout(timer)
    timer = undefined
  }

  /** POST /sandbox/refresh(202) 뒤 상태를 바로 다시 읽는다. building 동안은 2초마다 읽는다. */
  async function refresh(): Promise<void> {
    refreshing.value = true
    try {
      await refreshSandbox()
      statusError.value = null
    } catch (e) {
      statusError.value = e instanceof ApiError ? describeApiError(e) : String(e)
    } finally {
      refreshing.value = false
      await poll()
    }
  }

  return { status, statusError, schema, schemaError, refreshing, tableIndex, findTable, start, stop, poll, refresh }
})
