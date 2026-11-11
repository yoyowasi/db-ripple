<script setup lang="ts">
// C3 diff 그리드. 발동 보기(event, 묶음별 행 변경)와 최종 차이 보기(net, 순 변경)를 전환하고 서버 페이지로 읽는다.
import { AgGridVue } from 'ag-grid-vue3'
import type { GetRowIdParams, GridApi, GridReadyEvent, RowClassParams } from 'ag-grid-community'
import { computed, ref, watch } from 'vue'
import type { NetChange, PreviewResult, RowChange, RowView } from '../api/types'
import { useRowPage } from '../composables/useRowPage'
import { formatCount } from '../lib/format'
import type { GridRow } from '../lib/gridRows'
import {
  buildColumnDefs,
  columnMeta,
  dataColumns,
  eventRowToGrid,
  netRowToGrid,
} from '../lib/gridRows'
import { gridTheme } from '../lib/theme'
import { useSandboxStore } from '../stores/sandbox'
import ProblemBanner from './ProblemBanner.vue'

const props = defineProps<{
  preview: PreviewResult | null
  selectedFiringId: string | null
  view: RowView
  showZeroRows: boolean
  /** 서버에서 이미 지워졌다(적용·취소). 마지막으로 읽은 페이지만 보인다. */
  consumed: boolean
}>()

const emit = defineEmits<{
  'update:selectedFiringId': [id: string | null]
  'update:view': [view: RowView]
}>()

const sandbox = useSandboxStore()

const previewId = computed(() => props.preview?.previewId ?? null)
const viewRef = computed(() => props.view)
const firingRef = computed(() => props.selectedFiringId)
const enabled = computed(() => !!props.preview && !props.consumed)

const { page, loading, error, offset, limit, next, prev } = useRowPage(previewId, viewRef, firingRef, enabled)

const firingTable = computed(() => new Map((props.preview?.firings ?? []).map((f) => [f.id, f.table])))

const gridRows = computed<GridRow[]>(() => {
  const p = page.value
  if (!p) return []
  if (p.view === 'net') {
    return (p.rows as NetChange[]).map((r, i) => netRowToGrid(r, p.offset + i, columnMeta(sandbox.findTable(r.table))))
  }
  return (p.rows as RowChange[]).map((r) => {
    const table = firingTable.value.get(r.firingId) ?? ''
    return eventRowToGrid(r, table, columnMeta(sandbox.findTable(table)))
  })
})

const columnDefs = computed(() => {
  const rows = gridRows.value
  const tables = sandbox.schema?.tables ?? []
  const first = rows[0] ? sandbox.findTable(rows[0].table) : undefined
  const schemaColumns = new Map((first?.columns ?? []).map((c) => [c.name, c]))
  const mixed = new Set(rows.map((r) => r.table)).size > 1
  return buildColumnDefs({
    showTable: props.view === 'net' || mixed,
    columns: dataColumns(rows, tables),
    schemaColumns,
  })
})

const defaultColDef = { resizable: true, sortable: false, suppressMovable: true }
const getRowId = (p: GetRowIdParams<GridRow>): string => p.data.rowId
const getRowClass = (p: RowClassParams<GridRow>): string | undefined => {
  const id = props.selectedFiringId
  return props.view === 'net' && id && p.data?.firingIds.includes(id) ? 'row-linked' : undefined
}

let api: GridApi<GridRow> | null = null
function onGridReady(e: GridReadyEvent<GridRow>): void {
  api = e.api
}
// 최종 차이 보기에서 선택한 묶음이 바뀌면 강조 줄만 다시 그린다.
watch(
  () => props.selectedFiringId,
  () => api?.redrawRows(),
)

// 묶음 선택 목록: 0행 묶음은 기본 숨김(E23). 선택돼 있으면 항상 보인다.
const firingOptions = computed(() => {
  const list = (props.preview?.firings ?? [])
    .filter((f) => props.showZeroRows || f.rowCount > 0 || f.id === props.selectedFiringId)
    .sort((a, b) => a.captureSeq - b.captureSeq)
  return list.map((f) => ({
    id: f.id,
    label: `#${f.captureSeq} ${f.table} ${f.operation} ${formatCount(f.rowCount)}행 (깊이 ${f.depth})`,
  }))
})

function onFiringChange(e: Event): void {
  const v = (e.target as HTMLSelectElement).value
  emit('update:selectedFiringId', v === '' ? null : v)
}

// E6: 키 없는 테이블의 다중집합 문구. 최종 차이 보기는 모든 테이블, 발동 보기는 선택한 묶음의 테이블만.
const multisetNotes = computed(() => {
  const p = props.preview
  if (!p) return []
  const selTable = p.firings.find((f) => f.id === props.selectedFiringId)?.table
  return p.netSummary
    .filter((s) => (s.multisetRemoved > 0 || s.multisetAdded > 0) && (props.view === 'net' || s.table === selTable))
    .map((s) => ({
      table: s.table,
      text: `제거된 행 ${formatCount(s.multisetRemoved)}개, 추가된 행 ${formatCount(s.multisetAdded)}개`,
    }))
})

const range = computed(() => {
  const p = page.value
  if (!p || p.total === 0) return '0행'
  const to = Math.min(p.offset + p.rows.length, p.total)
  return `${formatCount(p.offset + 1)}–${formatCount(to)} / ${formatCount(p.total)}행`
})
const canPrev = computed(() => !props.consumed && offset.value > 0)
const canNext = computed(() => !props.consumed && !!page.value && offset.value + limit.value < page.value.total)
</script>

<template>
  <section class="card" aria-label="변경 행">
    <header>
      <h2>변경 행</h2>
      <fieldset class="seg">
        <legend class="sr-only">보기 방식</legend>
        <label :class="{ on: view === 'event' }">
          <input type="radio" name="rowview" value="event" :checked="view === 'event'" :disabled="consumed" @change="emit('update:view', 'event')" />
          발동 보기
        </label>
        <label :class="{ on: view === 'net' }">
          <input type="radio" name="rowview" value="net" :checked="view === 'net'" :disabled="consumed" @change="emit('update:view', 'net')" />
          최종 차이 보기
        </label>
      </fieldset>
    </header>

    <div class="tools">
      <label v-if="view === 'event'" class="field">
        <span>묶음</span>
        <select :value="selectedFiringId ?? ''" :disabled="!preview || consumed" @change="onFiringChange">
          <option value="">전체(모든 묶음)</option>
          <option v-for="o in firingOptions" :key="o.id" :value="o.id">{{ o.label }}</option>
        </select>
      </label>
      <span v-else class="muted">테이블 전체의 순 변경을 보입니다. 그래프에서 고른 묶음과 관련된 행은 왼쪽 띠로 표시됩니다.</span>

      <span class="spacer"></span>
      <span class="muted" role="status">{{ loading ? '읽는 중…' : range }}</span>
      <label class="field">
        <span>페이지 크기</span>
        <select v-model.number="limit" :disabled="consumed">
          <option :value="50">50</option>
          <option :value="100">100</option>
          <option :value="500">500</option>
          <option :value="1000">1000</option>
        </select>
      </label>
      <button type="button" class="btn btn-sm" :disabled="!canPrev" @click="prev()">이전</button>
      <button type="button" class="btn btn-sm" :disabled="!canNext" @click="next()">다음</button>
    </div>

    <div class="notes">
      <p class="muted hint">
        <span class="legend-chip changed">바뀐 셀</span>은 "이전 → 이후"로 보입니다.
        <span class="legend-chip volatile">예상값</span>은 원본에 적용하면 달라질 수 있는 값(IDENTITY·SEQUENCE·GETDATE·NEWID·rowversion),
        <span class="legend-chip derived">파생</span>은 계산 열입니다.
        같은 행을 여러 문장이 바꾸면 발동 보기는 문장별 중간 값을, 최종 차이 보기는 순 변경을 보입니다.
      </p>
      <div v-for="n in multisetNotes" :key="n.table" class="banner banner-info">
        <div>
          <strong>{{ n.table }}: {{ n.text }}</strong>
          키가 없는 테이블이라 행 개수 차이로만 비교합니다. UPDATE의 전후 짝은 확정하지 않습니다.
        </div>
      </div>
      <div v-if="consumed" class="banner banner-warn" role="status">
        <div>이 미리보기는 서버에서 지워져 마지막으로 읽은 페이지만 보입니다.</div>
      </div>
      <ProblemBanner v-if="error" :error="error" title="행을 읽지 못했습니다." />
    </div>

    <div v-if="!preview" class="empty">미리보기를 실행하면 변경 행이 여기에 나옵니다.</div>
    <div v-else class="grid-host" role="region" aria-label="변경 행 표">
      <AgGridVue
        class="grid"
        :theme="gridTheme"
        :row-data="gridRows"
        :column-defs="columnDefs"
        :default-col-def="defaultColDef"
        :get-row-id="getRowId"
        :get-row-class="getRowClass"
        :tooltip-show-delay="400"
        :loading="loading"
        overlay-no-rows-template="<span>표시할 행이 없습니다</span>"
        @grid-ready="onGridReady"
      />
    </div>
  </section>
</template>

<style scoped>
.seg {
  display: inline-flex;
  border: 1px solid var(--border);
  border-radius: var(--radius);
  padding: 0;
  margin: 0;
  overflow: hidden;
}
.seg label {
  position: relative;
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 3px 10px;
  font-size: 13px;
  cursor: pointer;
}
.seg label.on {
  background: var(--accent);
  color: var(--accent-fg);
  font-weight: 600;
}
.seg label:focus-within {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}
.seg input {
  /* 라디오는 시각적으로 숨기고 라벨 전체를 버튼처럼 쓴다. 키보드(방향키·스페이스)는 그대로 된다. */
  position: absolute;
  opacity: 0;
  pointer-events: none;
}
.tools {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  padding: 8px 12px;
}
.field {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 12.5px;
}
.field select {
  max-width: 360px;
}
.spacer {
  flex: 1;
}
.notes {
  display: grid;
  gap: 6px;
  padding: 0 12px 8px;
}
.hint {
  margin: 0;
  font-size: 12px;
}
.legend-chip {
  padding: 0 5px;
  border-radius: 8px;
  font-weight: 600;
}
.legend-chip.changed {
  background: var(--changed-bg);
  color: var(--fg);
}
.legend-chip.volatile {
  background: var(--volatile-bg);
  color: var(--volatile);
}
.legend-chip.derived {
  background: var(--derived-bg);
  color: var(--derived);
}
.empty {
  padding: 32px 12px;
  text-align: center;
  color: var(--fg-muted);
}
.grid-host {
  height: 380px;
  padding: 0 12px 12px;
}
.grid {
  width: 100%;
  height: 100%;
}
</style>
