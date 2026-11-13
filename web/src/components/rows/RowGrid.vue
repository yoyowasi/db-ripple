<script setup lang="ts">
// 행 표(AG Grid). 한 페이지의 행을 받아 열·뱃지를 정하고 그린다. 가로 스크롤은 표 안에서만 생긴다.
import { AgGridVue } from 'ag-grid-vue3'
import type { GetRowIdParams, GridApi, GridReadyEvent, RowClassParams } from 'ag-grid-community'
import { computed, watch } from 'vue'
import type { GridRow } from '../../lib/gridRows'
import { buildColumnDefs, columnTags, dataColumns } from '../../lib/gridRows'
import { useSandboxStore } from '../../stores/sandbox'
import { rowsGridTheme } from './gridTheme'

const props = defineProps<{
  rows: GridRow[]
  loading: boolean
  /** 표 이름(화면 읽기 프로그램용) */
  label: string
  /** 최종 차이 보기에서 이 묶음이 바꾼 행에 왼쪽 띠를 붙인다 */
  linkedFiringId?: string | null
}>()

const sandbox = useSandboxStore()

const columnDefs = computed(() => {
  const rows = props.rows
  const first = rows[0] ? sandbox.findTable(rows[0].table) : undefined
  const schemaColumns = new Map((first?.columns ?? []).map((c) => [c.name, c]))
  const mixed = new Set(rows.map((r) => r.table)).size > 1
  const columns = dataColumns(rows, sandbox.schema?.tables ?? [])
  return buildColumnDefs({ showTable: mixed, columns, schemaColumns, tags: columnTags(rows, columns), rows })
})

const defaultColDef = { resizable: true, sortable: false, suppressMovable: true, tooltipShowDelay: 400 }
const getRowId = (p: GetRowIdParams<GridRow>): string => p.data.rowId
const getRowClass = (p: RowClassParams<GridRow>): string | undefined => {
  const r = p.data
  if (!r) return undefined
  if (props.linkedFiringId && r.firingIds.includes(props.linkedFiringId)) return 'r-linked'
  return undefined
}

// 행이 적으면 표도 낮게(머리 30 + 행 28 + 가로 스크롤바 여유), 많으면 460px에서 안쪽 스크롤.
const height = computed(() => Math.min(460, Math.max(120, 30 + props.rows.length * 28 + 20)))

let api: GridApi<GridRow> | null = null
function onGridReady(e: GridReadyEvent<GridRow>): void {
  api = e.api
}
watch(
  () => props.linkedFiringId,
  () => api?.redrawRows(),
)
</script>

<template>
  <div class="gh" role="region" :aria-label="`${label} 행 표(가로로 스크롤)`" :style="{ height: `${height}px` }">
    <AgGridVue
      class="grid"
      :theme="rowsGridTheme"
      :row-data="rows"
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
</template>

<style scoped>
.gh {
  min-height: 0;
}
.grid {
  width: 100%;
  height: 100%;
}
.gh :deep(.cell) {
  display: block;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.gh :deep(.op-text) {
  font-family: var(--ui);
  font-weight: 700;
}
.gh :deep(.cell-op.op-insert),
.gh :deep(.cell-op.op-multiset_added) {
  color: var(--add-fg);
}
.gh :deep(.cell-op.op-delete),
.gh :deep(.cell-op.op-multiset_removed) {
  color: var(--del-fg);
}
.gh :deep(.cell-op.op-update) {
  color: var(--fg);
}
.gh :deep(.cell-op .chip) {
  font-family: var(--ui);
  font-weight: 600;
}
/* 이전 값 취소선(--del-*), 이후 값(--add-*). 색만으로 알리지 않도록 앞말(sr-only)과 취소선을 함께 쓴다. */
.gh :deep(del) {
  background: var(--del-bg);
  color: var(--del-fg);
  text-decoration: line-through;
  text-decoration-thickness: 1px;
}
.gh :deep(ins) {
  background: var(--add-bg);
  color: var(--add-fg);
  text-decoration: none;
}
.gh :deep(.c-chg) {
  background: var(--chg-bg);
}
.gh :deep(.arrow) {
  color: var(--muted);
}
.gh :deep(.v-null) {
  font-style: italic;
}
.gh :deep(.v-none) {
  color: var(--muted);
}
.gh :deep(.chip-vol) {
  font-family: var(--ui);
  background: transparent;
  color: var(--fg);
  border-color: var(--muted);
}
.gh :deep(.r-linked) {
  box-shadow: inset 3px 0 0 var(--accent);
}
.gh :deep(.ag-header-cell-label) {
  min-width: 0;
}
</style>
