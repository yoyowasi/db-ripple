<script setup lang="ts">
// "테이블별 변경 행 수(순변경)" 표. 행 hover → 그래프 강조, 클릭 → 그 테이블의 첫 묶음을 서랍에 연다.
import { computed, onBeforeUnmount } from 'vue'
import type { PreviewResult } from '../../api/types'
import { formatCount } from '../../lib/format'
import { netTotals } from '../../lib/summary'
import { usePreviewStore } from '../../stores/preview'
import { mergeNetRows, rowOpText, splitTable } from './rowOps'

const props = defineProps<{ preview: PreviewResult; basis: ReadonlyMap<string, string> }>()
const store = usePreviewStore()

/** 테이블의 표시 순번이 가장 앞선, 행 있는 묶음(id와 순번) */
function firstFiring(table: string): { id: string | null; n: number } {
  let best: string | null = null
  let bestN = Infinity
  for (const f of props.preview.firings) {
    if (f.table !== table || f.rowCount === 0) continue
    const n = store.firingOrder.get(f.id) ?? Infinity
    if (n < bestN) {
      best = f.id
      bestN = n
    }
  }
  return { id: best, n: bestN }
}

const rows = computed(() =>
  mergeNetRows(props.preview.netSummary)
    .map((r) => ({ r, total: netTotals([r]).total }))
    .filter((x) => x.total > 0)
    .map(({ r, total }) => {
      const f = firstFiring(r.table)
      return {
        table: r.table,
        ...splitTable(r.table),
        op: rowOpText(r),
        total,
        basis: props.basis.get(r.table) ?? null,
        firingId: f.id,
        n: f.n,
      }
    })
    // 그래프·발동 목록과 같은 표시 순번 순
    .sort((a, b) => (a.n === b.n ? 0 : a.n - b.n)),
)
const total = computed(() => netTotals(props.preview.netSummary).total)
const selectedTable = computed(() => props.preview.firings.find((f) => f.id === store.selectedFiringId)?.table ?? null)

function open(firingId: string | null): void {
  if (firingId) store.selectFiring(firingId, { openDrawer: true })
}

onBeforeUnmount(() => store.setHoveredTable(null))
</script>

<template>
  <table class="rsum">
    <caption class="sr-only">테이블별 순변경 행 수와 발동 기준</caption>
    <colgroup>
      <col />
      <col class="c-n" />
      <col class="c-b" />
    </colgroup>
    <thead>
      <tr>
        <th scope="col">테이블</th>
        <th scope="col">행</th>
        <th scope="col" class="basis">발동 기준</th>
      </tr>
    </thead>
    <tbody>
      <tr
        v-for="x in rows"
        :key="x.table"
        :class="{ hl: store.hoveredTable === x.table, go: x.firingId }"
        @mouseenter="store.setHoveredTable(x.table)"
        @mouseleave="store.setHoveredTable(null)"
        @focusin="store.setHoveredTable(x.table)"
        @focusout="store.setHoveredTable(null)"
        @click="open(x.firingId)"
      >
        <th scope="row" class="nm">
          <button
            v-if="x.firingId"
            type="button"
            class="nmbtn"
            :title="x.table"
            :aria-current="selectedTable === x.table ? 'true' : undefined"
          >
            <span class="id">{{ x.name }}</span>
            <small>{{ x.schema }} · {{ x.op }}</small>
          </button>
          <span v-else class="nmbtn" :title="x.table">
            <span class="id">{{ x.name }}</span>
            <small>{{ x.schema }} · {{ x.op }}</small>
          </span>
        </th>
        <td class="n">{{ formatCount(x.total) }}</td>
        <td class="basis">
          <template v-if="x.basis">{{ x.basis }}</template>
          <span v-else role="img" aria-label="해당 없음">·</span>
        </td>
      </tr>
    </tbody>
    <tfoot>
      <tr>
        <th scope="row">합계(순변경)</th>
        <td class="n">{{ formatCount(total) }}</td>
        <td></td>
      </tr>
    </tfoot>
  </table>
</template>

<style scoped>
.rsum {
  width: 100%;
  table-layout: fixed;
  border-collapse: collapse;
  font-variant-numeric: tabular-nums;
}
.c-n {
  width: 48px;
}
.c-b {
  width: 88px;
}
.rsum thead th {
  padding: 2px 4px 4px;
  border-bottom: 1px solid var(--line);
  font: 500 12px var(--ui);
  color: var(--muted);
  text-align: right;
  vertical-align: bottom;
}
.rsum thead th:first-child {
  padding-left: 0;
  text-align: left;
}
.rsum thead th:last-child,
.rsum td:last-child {
  padding-right: 0;
}
.rsum td {
  padding: 5px 4px;
  border-bottom: 1px solid var(--sunk);
  text-align: right;
  vertical-align: top;
}
.rsum td.n {
  font: 13px var(--mono);
}
.rsum td.basis {
  font: 12px var(--ui);
  color: var(--muted);
  overflow-wrap: anywhere;
}
.rsum th.nm {
  padding: 0;
  border-bottom: 1px solid var(--sunk);
  text-align: left;
  font-weight: 400;
  overflow: hidden;
  vertical-align: top;
}
.nmbtn {
  display: block;
  width: 100%;
  min-width: 0;
  padding: 5px 4px 5px 0;
  border: 0;
  background: none;
  color: inherit;
  font: inherit;
  text-align: left;
}
button.nmbtn {
  cursor: pointer;
}
button.nmbtn:focus-visible {
  outline-offset: -2px;
}
.nmbtn .id {
  display: block;
  font: 500 12px var(--mono);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.nmbtn small {
  display: block;
  font: 12px var(--ui);
  color: var(--muted);
  overflow-wrap: anywhere;
}
.rsum tbody tr.go {
  cursor: pointer;
}
.rsum tbody tr:hover > *,
.rsum tbody tr.hl > * {
  background: color-mix(in srgb, var(--accent) 9%, transparent);
}
.rsum tbody .nmbtn[aria-current='true'] .id {
  text-decoration: underline;
  text-underline-offset: 3px;
}
.rsum tfoot th,
.rsum tfoot td {
  padding: 5px 4px;
  border-top: 1px solid var(--tick);
  font-weight: 700;
}
.rsum tfoot th {
  padding-left: 0;
  text-align: left;
  font-family: var(--ui);
}
.rsum tfoot td.n {
  font-family: var(--mono);
}
</style>
