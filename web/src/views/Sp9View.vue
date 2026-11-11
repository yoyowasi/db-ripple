<script setup lang="ts">
// SP9 개발용 측정 화면(/sp9). 서버 없이 합성 데이터로 그래프(노드 50·200·500)와 그리드(1만·5만 행)의 렌더 시간을 잰다.
// 값은 m5.csv의 브라우저 열에 수동으로 옮긴다(Q42). 측정 전 다른 탭을 닫고, 같은 크기를 3회 이상 반복해 중앙값을 쓴다.
import { AgGridVue } from 'ag-grid-vue3'
import type { GetRowIdParams, GridApi, GridReadyEvent } from 'ag-grid-community'
import { computed, nextTick, ref, shallowRef } from 'vue'
import type { Firing, GraphDto } from '../api/types'
import PropagationGraph from '../components/PropagationGraph.vue'
import { formatMs } from '../lib/format'
import type { GridRow } from '../lib/gridRows'
import { buildColumnDefs } from '../lib/gridRows'
import { makeGridRows, makeSyntheticGraph, synthColumns } from '../lib/sp9Data'
import { gridTheme } from '../lib/theme'

interface Measure {
  at: string
  kind: 'graph' | 'grid-render' | 'grid-scroll' | 'grid-sort'
  size: number
  nodes: number | null
  edges: number | null
  layoutMs: number | null
  ms: number
  heapMb: number | null
}

const results = ref<Measure[]>([])
const busy = ref(false)

function heapMb(): number | null {
  const m = (performance as unknown as { memory?: { usedJSHeapSize: number } }).memory
  return m ? Math.round(m.usedJSHeapSize / 1048576) : null
}
const nextFrames = (): Promise<void> =>
  new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(() => r())))

function record(m: Omit<Measure, 'at' | 'heapMb'>): void {
  results.value.unshift({ ...m, at: new Date().toLocaleTimeString('ko-KR', { hour12: false }), heapMb: heapMb() })
}

// ---- 그래프 ----
const graph = shallowRef<GraphDto | null>(null)
const firings = shallowRef<Firing[]>([])
const graphSize = ref(0)
let graphStart = 0
let pendingLayoutMs = 0
let pendingStats = { nodes: 0, edges: 0 }
let graphWaiting = false

async function measureGraph(n: number): Promise<void> {
  if (busy.value) return
  busy.value = true
  graph.value = null // 이전 그래프를 먼저 치워 처음부터 다시 그리게 한다
  await nextTick()
  await nextFrames()
  const data = makeSyntheticGraph(n)
  graphSize.value = n
  graphWaiting = true
  graphStart = performance.now()
  graph.value = data.graph
  firings.value = data.firings
}

function onLayoutDone(s: { layoutMs: number; nodes: number; edges: number }): void {
  if (!graphWaiting) return
  pendingLayoutMs = s.layoutMs
  pendingStats = { nodes: s.nodes, edges: s.edges }
}

function onPainted(): void {
  if (!graphWaiting) return
  graphWaiting = false
  record({
    kind: 'graph',
    size: graphSize.value,
    nodes: pendingStats.nodes,
    edges: pendingStats.edges,
    layoutMs: pendingLayoutMs,
    ms: performance.now() - graphStart,
  })
  busy.value = false
}

// ---- 그리드 ----
const columns = synthColumns()
const columnDefs = buildColumnDefs({ showTable: false, columns, schemaColumns: new Map() })
const defaultColDef = { resizable: true, sortable: true }
const getRowId = (p: GetRowIdParams<GridRow>): string => p.data.rowId
const userAgent = navigator.userAgent
const rows = shallowRef<GridRow[]>([])
const gridSize = ref(0)
let gridApi: GridApi<GridRow> | null = null
let gridStart = 0
let gridWaiting = false

function onGridReady(e: GridReadyEvent<GridRow>): void {
  gridApi = e.api
}

async function measureGrid(n: number): Promise<void> {
  if (busy.value) return
  busy.value = true
  rows.value = []
  await nextTick()
  await nextFrames()
  const data = makeGridRows(n)
  gridSize.value = n
  gridWaiting = true
  gridStart = performance.now()
  rows.value = data
}

async function onFirstDataRendered(): Promise<void> {
  if (!gridWaiting) return
  gridWaiting = false
  await nextFrames()
  record({ kind: 'grid-render', size: gridSize.value, nodes: null, edges: null, layoutMs: null, ms: performance.now() - gridStart })
  busy.value = false
}

async function measureScroll(): Promise<void> {
  if (!gridApi || busy.value || gridSize.value === 0) return
  busy.value = true
  const t0 = performance.now()
  gridApi.ensureIndexVisible(gridSize.value - 1, 'bottom')
  await nextFrames()
  record({ kind: 'grid-scroll', size: gridSize.value, nodes: null, edges: null, layoutMs: null, ms: performance.now() - t0 })
  gridApi.ensureIndexVisible(0, 'top')
  busy.value = false
}

async function measureSort(): Promise<void> {
  if (!gridApi || busy.value || gridSize.value === 0) return
  busy.value = true
  const t0 = performance.now()
  gridApi.applyColumnState({ state: [{ colId: 'c:C03', sort: 'desc' }], defaultState: { sort: null } })
  await nextFrames()
  record({ kind: 'grid-sort', size: gridSize.value, nodes: null, edges: null, layoutMs: null, ms: performance.now() - t0 })
  busy.value = false
}

// ---- 결과 복사 ----
const KIND_LABEL: Record<Measure['kind'], string> = {
  graph: '그래프(노드)',
  'grid-render': '그리드 첫 렌더(행)',
  'grid-scroll': '그리드 끝까지 스크롤',
  'grid-sort': '그리드 정렬',
}

const csv = computed(() => {
  const head = 'time,kind,size,nodes,edges,layout_ms,total_ms,heap_mb'
  const lines = results.value
    .slice()
    .reverse()
    .map((m) => [m.at, m.kind, m.size, m.nodes ?? '', m.edges ?? '', m.layoutMs === null ? '' : m.layoutMs.toFixed(1), m.ms.toFixed(1), m.heapMb ?? ''].join(','))
  return [head, ...lines].join('\n')
})
const copied = ref('')
async function copyCsv(): Promise<void> {
  try {
    await navigator.clipboard.writeText(csv.value)
    copied.value = '복사했습니다.'
  } catch {
    copied.value = '복사하지 못했습니다. 아래 상자에서 직접 선택해 복사하세요.'
  }
}
</script>

<template>
  <div class="sp9">
    <div class="banner banner-info">
      <div>
        <strong>SP9 렌더 측정(개발용)</strong>
        합성 데이터로 브라우저 렌더 시간을 잽니다. 서버가 필요 없습니다. 측정 전에 다른 탭·확장 프로그램을 닫고, 같은 크기를 3회 이상 잰 중앙값을
        m5.csv의 브라우저 열에 수동으로 옮기세요. 브라우저와 PC 사양(User-Agent는 결과 표 아래에 보입니다)을 함께 적습니다.
      </div>
    </div>

    <section class="card" aria-label="그래프 측정">
      <header>
        <h2>그래프: 노드 수별 배치+렌더</h2>
        <button v-for="n in [50, 200, 500]" :key="n" type="button" class="btn btn-sm" :disabled="busy" @click="measureGraph(n)">
          {{ n }}개 측정
        </button>
      </header>
      <PropagationGraph
        flow-id="sp9-graph"
        :graph="graph"
        :firings="firings"
        :selected-firing-id="null"
        :show-zero-rows="true"
        :height="420"
        @layout-done="onLayoutDone"
        @painted="onPainted"
        @update:show-zero-rows="() => {}"
      />
    </section>

    <section class="card" aria-label="그리드 측정">
      <header>
        <h2>그리드: 행 수별 첫 렌더·스크롤·정렬</h2>
        <button v-for="n in [10000, 50000]" :key="n" type="button" class="btn btn-sm" :disabled="busy" @click="measureGrid(n)">
          {{ n.toLocaleString('ko-KR') }}행 렌더
        </button>
        <button type="button" class="btn btn-sm" :disabled="busy || gridSize === 0" @click="measureScroll()">끝까지 스크롤</button>
        <button type="button" class="btn btn-sm" :disabled="busy || gridSize === 0" @click="measureSort()">C03 정렬</button>
      </header>
      <div class="grid-host">
        <AgGridVue
          class="grid"
          :theme="gridTheme"
          :row-data="rows"
          :column-defs="columnDefs"
          :default-col-def="defaultColDef"
          :get-row-id="getRowId"
          @grid-ready="onGridReady"
          @first-data-rendered="onFirstDataRendered"
        />
      </div>
    </section>

    <section class="card" aria-label="측정 결과">
      <header>
        <h2>측정 결과</h2>
        <button type="button" class="btn btn-sm" :disabled="results.length === 0" @click="copyCsv()">CSV 복사</button>
        <button type="button" class="btn btn-sm" :disabled="results.length === 0" @click="results = []">지우기</button>
        <span class="muted" role="status">{{ copied }}</span>
      </header>
      <div class="card-body">
        <p v-if="results.length === 0" class="muted">아직 측정값이 없습니다.</p>
        <table v-else class="data">
          <thead>
            <tr>
              <th>시각</th><th>종류</th><th class="num">크기</th><th class="num">노드</th><th class="num">간선</th>
              <th class="num">배치(dagre)</th><th class="num">전체</th><th class="num">JS 힙(MB)</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="(m, i) in results" :key="i">
              <td>{{ m.at }}</td>
              <td>{{ KIND_LABEL[m.kind] }}</td>
              <td class="num">{{ m.size.toLocaleString('ko-KR') }}</td>
              <td class="num">{{ m.nodes ?? '-' }}</td>
              <td class="num">{{ m.edges ?? '-' }}</td>
              <td class="num">{{ m.layoutMs === null ? '-' : formatMs(m.layoutMs) }}</td>
              <td class="num"><strong>{{ formatMs(m.ms) }}</strong></td>
              <td class="num">{{ m.heapMb ?? '-' }}</td>
            </tr>
          </tbody>
        </table>
        <label class="csv-label">
          <span class="muted">CSV(복사용)</span>
          <textarea class="csv mono" readonly rows="5" :value="csv"></textarea>
        </label>
        <p class="muted ua">User-Agent: {{ userAgent }}</p>
      </div>
    </section>
  </div>
</template>

<style scoped>
.sp9 {
  display: grid;
  gap: var(--gutter);
  max-width: 1400px;
}
.grid-host {
  height: 380px;
  padding: 8px 12px 12px;
}
.grid {
  width: 100%;
  height: 100%;
}
.csv-label {
  display: grid;
  gap: 4px;
  margin-top: 10px;
}
.csv {
  width: 100%;
  background: var(--bg-sunken);
  color: var(--fg);
  border: 1px solid var(--border);
  border-radius: var(--radius);
  padding: 6px;
}
.ua {
  font-size: 12px;
  overflow-wrap: anywhere;
}
</style>
