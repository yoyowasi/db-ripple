<script setup lang="ts">
// C2 전파 그래프(목업 v6). 왼쪽 → 오른쪽(dagre), 노드는 결과를 말하고 간선 라벨은 원인을 말한다.
// 노드를 누르거나 Enter → select-firing 이벤트(선택·서랍은 부모가 정한다: 전역 스토어를 쓰지 않아 /sp9에서도 안전). 마우스·포커스·hoveredTable → 문장부터 그 노드까지 경로 강조.
import { VueFlow, useVueFlow } from '@vue-flow/core'
import type { NodeMouseEvent } from '@vue-flow/core'
import { computed, markRaw, nextTick, onBeforeUnmount, provide, shallowRef, useTemplateRef, watch } from 'vue'
import type { Firing, GraphDto } from '../api/types'
import { ancestorsOf, buildFlow, legendOf, summaryLine } from '../lib/graphModel'
import type { FlowModel, RippleNodeData } from '../lib/graphModel'
import type { EdgeTone } from '../lib/graphStyle'
import { displayOrder } from '../lib/summary'
import FlowNode from './FlowNode.vue'
import GraphLegend from './GraphLegend.vue'
import RippleEdge from './RippleEdge.vue'
import { GRAPH_CONTEXT } from './graph/context'

const props = withDefaults(
  defineProps<{
    graph: GraphDto | null
    firings: Firing[]
    selectedFiringId: string | null
    showZeroRows: boolean
    /** 표시 순번(묶음 id → 순번). 주지 않으면 firings로 계산한다. */
    order?: ReadonlyMap<string, number>
    /** 레일·탐색기에서 마우스를 올린 테이블("스키마.테이블"). 그 테이블 노드의 경로를 강조한다. */
    hoveredTable?: string | null
    /** 고정 높이(px). 주지 않으면 부모 높이를 채운다. */
    height?: number
    flowId?: string
  }>(),
  { height: undefined, flowId: 'ripple-graph', order: undefined, hoveredTable: null },
)

const emit = defineEmits<{
  /** 노드·멤버를 골랐다(그래프는 스토어를 바꾸지 않는다) */
  'select-firing': [firingId: string]
  /** [표로 보기] */
  'show-rows': []
  'update:showZeroRows': [value: boolean]
  /** 배치(dagre)가 끝났다 */
  'layout-done': [stats: { layoutMs: number; nodes: number; edges: number }]
  /** 배치 뒤 두 프레임이 지나 화면에 그려졌다(SP9 측정용) */
  painted: []
}>()

// markRaw: 컴포넌트 정의를 반응형으로 만들지 않는다.
const nodeTypes = { ripple: markRaw(FlowNode) } as never
const edgeTypes = { ripple: markRaw(RippleEdge) } as never
const TONES: EdgeTone[] = ['direct', 'call', 'fk', 'fires', 'blocked', 'residual']
const FIT = { padding: 0.05, maxZoom: 1 }

const { fitView, zoomIn, zoomOut, setViewport, viewport } = useVueFlow({ id: props.flowId })

/** 표시 순번: 부모가 준 값 그대로, 없으면 같은 규칙으로 계산 */
const order = computed(() => props.order ?? displayOrder(props.firings))

const model = shallowRef<FlowModel | null>(null)
/** 축소 표시(배율 80% 미만): 좁은 노드로 다시 배치하고 부가 줄을 숨긴다(목업 v6). */
const compact = shallowRef(false)
const drawing = shallowRef(false)
const hoveredId = shallowRef<string | null>(null)
const focusedId = shallowRef<string | null>(null)
const groupId = shallowRef<string | null>(null)
const wrap = useTemplateRef<HTMLDivElement>('wrap')
/** 마지막 자동 맞춤 뒤의 화면 위치. 지금 위치가 이것과 다르면 사용자가 움직인 것으로 보고 크기 변화에 다시 맞추지 않는다. */
let fitted: { x: number; y: number; zoom: number } | null = null
let fitting = false
let drawTimer: number | undefined

const COMPACT_BELOW = 0.8
const reducedMotion = () => window.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false
const twoFrames = () => new Promise<void>((r) => requestAnimationFrame(() => requestAnimationFrame(() => r())))

function build(c: boolean): FlowModel | null {
  return props.graph ? buildFlow(props.graph, props.firings, props.showZeroRows, order.value, c) : null
}

/** 맞춤 배율 어림(Vue Flow fitView와 같은 식). 그래프 영역 크기를 모르면 null */
function fitZoomOf(m: FlowModel): number | null {
  const el = wrap.value
  if (!el || !el.clientWidth || !el.clientHeight || !m.nodes.length) return null
  let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity
  for (const n of m.nodes) {
    const d = n.data!
    x0 = Math.min(x0, n.position.x)
    y0 = Math.min(y0, n.position.y)
    x1 = Math.max(x1, n.position.x + d.width)
    y1 = Math.max(y1, n.position.y + d.boxHeight + (d.zeroChip ? 34 : 0))
  }
  return Math.min(FIT.maxZoom, el.clientWidth / ((x1 - x0) * (1 + FIT.padding)), el.clientHeight / ((y1 - y0) * (1 + FIT.padding)))
}

/** 보통 배치로 맞춤 배율을 어림해 80% 미만이면 축소 배치를 쓴다. */
function layoutForFit(): FlowModel | null {
  const full = build(false)
  const z = full ? fitZoomOf(full) : null
  compact.value = z !== null && z < COMPACT_BELOW
  return compact.value ? build(true) : full
}

async function fit(): Promise<void> {
  fitting = true
  await fitView(FIT)
  fitted = { ...viewport.value }
  await nextTick()
  fitting = false
}

/** [맞춤]·크기 변화: 축소 여부를 다시 정하고 맞춘다. */
async function refit(): Promise<void> {
  const before = compact.value
  const m = layoutForFit()
  if (compact.value !== before || !fitted) model.value = m
  await nextTick()
  await twoFrames()
  await fit()
}

function untouched(): boolean {
  const v = viewport.value
  return fitted !== null && Math.abs(v.x - fitted.x) < 1 && Math.abs(v.y - fitted.y) < 1 && Math.abs(v.zoom - fitted.zoom) < 0.001
}

watch(
  () => [props.graph, props.firings, props.showZeroRows, order.value] as const,
  async ([graph], old) => {
    if (!graph) {
      model.value = null
      return
    }
    let m = layoutForFit()!
    model.value = m
    groupId.value = null
    // 첫 그리기 연출: 새 그래프일 때만(0행 토글은 제외), 움직임 줄이기 설정이면 끈다.
    if (!old || old[0] !== graph) {
      window.clearTimeout(drawTimer)
      drawing.value = !reducedMotion()
      if (drawing.value) drawTimer = window.setTimeout(() => (drawing.value = false), 900)
    }
    await nextTick()
    await twoFrames()
    await fit()
    // 처음에는 그래프 영역 크기를 몰라 어림하지 못했다. 맞춘 배율로 다시 정한다.
    if (!compact.value && viewport.value.zoom < COMPACT_BELOW) {
      compact.value = true
      m = build(true)!
      model.value = m
      await nextTick()
      await twoFrames()
      await fit()
    }
    emit('layout-done', { layoutMs: m.layoutMs, nodes: m.nodes.length, edges: m.edges.length })
    emit('painted')
  },
  { immediate: true },
)

// 사용자가 확대·축소로 80%를 넘나들면 배치를 바꾸고, 첫 노드(문장)가 화면에서 제자리에 있도록 옮긴다.
watch(
  () => viewport.value.zoom,
  async (z) => {
    const m = model.value
    if (fitting || !m || !m.nodes.length || (z < COMPACT_BELOW) === compact.value) return
    if (fitted && Math.abs(z - fitted.zoom) < 0.001) return
    const v = viewport.value
    const root = m.nodes[0]
    const sx = v.x + v.zoom * root.position.x
    const sy = v.y + v.zoom * (root.position.y + root.data!.boxHeight / 2)
    compact.value = z < COMPACT_BELOW
    const next = build(compact.value)
    if (!next) return
    model.value = next
    const r = next.nodes.find((n) => n.id === root.id) ?? next.nodes[0]
    await nextTick()
    void setViewport({ x: sx - v.zoom * r.position.x, y: sy - v.zoom * (r.position.y + r.data!.boxHeight / 2), zoom: v.zoom })
  },
)

const nodes = computed(() => model.value?.nodes ?? [])
const edges = computed(() => model.value?.edges ?? [])
const nodeCount = computed(() => nodes.value.length)
const legend = computed(() => (model.value ? legendOf(model.value) : null))
const summary = computed(() => (props.graph ? summaryLine(props.graph, props.firings) : ''))
/** 접을 수 있는 0행 묶음 수(길목이라 늘 보이는 0행 묶음은 세지 않는다) */
const zeroTotal = computed(() => model.value?.collapsibleZeroRows ?? 0)

const zoomPct = computed(() => Math.round(viewport.value.zoom * 100))
const drawStep = computed(() => Math.min(0.14, 0.5 / Math.max(1, model.value?.maxRank ?? 1)))

// 경로 강조: 마우스 > 포커스 > 레일·탐색기 hover(hoveredTable)
const active = computed(() => {
  const m = model.value
  if (!m) return null
  const one = hoveredId.value ?? focusedId.value
  let ids: string[] = one ? [one] : []
  const table = props.hoveredTable
  if (!one && table) {
    ids = m.nodes.filter((n) => (n.data!.node.type === 'firing' || n.data!.node.type === 'residual') && n.data!.node.table === table).map((n) => n.id)
  }
  return ids.length ? ancestorsOf(ids, m.incoming) : null
})

provide(GRAPH_CONTEXT, {
  active,
  compact,
  drawing,
  drawStep,
  selectedFiringId: computed(() => props.selectedFiringId),
  showZeroRows: computed(() => props.showZeroRows),
  toggleZeroRows: () => emit('update:showZeroRows', !props.showZeroRows),
  markerId: (tone) => `${props.flowId}-mk-${tone}`,
})

function nodeData(id: string): RippleNodeData | undefined {
  return model.value?.nodes.find((n) => n.id === id)?.data
}

/** 노드 선택: 묶음이면 알리기(선택·서랍은 부모), 접힌 묶음이면 멤버 목록 */
function activate(id: string): void {
  const d = nodeData(id)
  if (!d) return
  if (d.node.type === 'firing') {
    emit('select-firing', d.node.firingId)
  } else if (d.node.type === 'group') {
    groupId.value = groupId.value === id ? null : id
  }
}

function onNodeClick({ node }: NodeMouseEvent): void {
  activate(node.id)
}

function nodeIdOf(t: EventTarget | null): string | null {
  const el = t instanceof Element ? t.closest('.vue-flow__node') : null
  return el?.getAttribute('data-id') ?? null
}
function onKeydown(e: KeyboardEvent): void {
  if (e.key !== 'Enter' && e.key !== ' ') return
  if (!(e.target instanceof Element) || !e.target.classList.contains('vue-flow__node')) return
  const id = nodeIdOf(e.target)
  if (!id) return
  e.preventDefault()
  activate(id)
}

function zoomBy(dir: 1 | -1): void {
  void (dir > 0 ? zoomIn() : zoomOut())
}

// 결과 패널·서랍 때문에 크기가 바뀌면, 사용자가 움직이지 않았을 때만 다시 맞춘다.
let ro: ResizeObserver | null = null
watch(wrap, (el) => {
  ro?.disconnect()
  if (!el) return
  ro = new ResizeObserver(() => {
    if (untouched()) void refit()
  })
  ro.observe(el)
})
onBeforeUnmount(() => {
  ro?.disconnect()
  window.clearTimeout(drawTimer)
})

const groupMembers = computed(() => {
  const d = groupId.value ? nodeData(groupId.value) : undefined
  if (!d || d.node.type !== 'group') return []
  const byId = new Map(props.graph?.nodes.map((n) => [n.id, n]) ?? [])
  return d.node.members.map((m) => {
    const n = byId.get(m)
    return { id: m, firingId: n?.type === 'firing' ? n.firingId : m, text: n?.type === 'firing' ? `${n.table} ${n.rowCount}행` : m }
  })
})
function pickMember(firingId: string): void {
  emit('select-firing', firingId)
}

const summaryId = computed(() => `${props.flowId}-sum`)
</script>

<template>
  <section class="pg" :class="{ fixed: height !== undefined }" aria-label="전파 그래프">
    <div class="gtool">
      <a class="lnk" href="#res-panel-rows" :aria-describedby="summaryId" @click.prevent="emit('show-rows')">표로 보기</a>
      <div class="grp" role="group" aria-label="그래프 보기 도구">
        <button type="button" class="btn btn-sm" :disabled="!nodeCount" @click="void refit()">
          <svg class="ic" viewBox="0 0 16 16" aria-hidden="true"><path d="M2 6V2h4M10 2h4v4M14 10v4h-4M6 14H2v-4" /></svg>맞춤
        </button>
        <button type="button" class="btn btn-sm" aria-label="확대" :disabled="!nodeCount" @click="zoomBy(1)">
          <svg class="ic" viewBox="0 0 16 16" aria-hidden="true"><path d="M8 3v10M3 8h10" /></svg>확대
        </button>
        <button type="button" class="btn btn-sm" aria-label="축소" :disabled="!nodeCount" @click="zoomBy(-1)">
          <svg class="ic" viewBox="0 0 16 16" aria-hidden="true"><path d="M3 8h10" /></svg>축소
        </button>
        <span class="zoomv" aria-live="polite" aria-label="현재 배율">{{ zoomPct }}%</span>
      </div>
      <label v-if="zeroTotal > 0" class="opt">
        <input type="checkbox" :checked="showZeroRows" @change="emit('update:showZeroRows', ($event.target as HTMLInputElement).checked)" />
        0행 묶음 보기<span v-if="!showZeroRows">({{ zeroTotal }}개 숨김)</span>
      </label>
    </div>

    <template v-if="graph && nodeCount > 0">
      <p :id="summaryId" class="gline"><b>한 줄 요약</b> {{ summary }}</p>
      <GraphLegend v-if="legend" class="lgd-wrap" :present="legend" />
    </template>

    <div v-if="!graph" class="empty">미리보기를 실행하면 전파 그래프가 여기에 나옵니다.</div>
    <div v-else-if="nodeCount === 0" class="empty">그릴 노드가 없습니다.</div>
    <div
      v-else
      ref="wrap"
      class="flow-wrap"
      :class="{ lo: compact, drawing, hov: active !== null }"
      :style="height !== undefined ? { height: `${height}px` } : undefined"
      @keydown="onKeydown"
      @focusin="focusedId = nodeIdOf($event.target)"
      @focusout="focusedId = null"
    >
      <svg class="mk-defs" aria-hidden="true">
        <defs>
          <marker
            v-for="t in TONES"
            :id="`${flowId}-mk-${t}`"
            :key="t"
            viewBox="0 0 10 10"
            refX="9"
            refY="5"
            markerWidth="7"
            markerHeight="7"
            orient="auto"
          >
            <path :class="`tone-${t}`" d="M0 1 L10 5 L0 9 z" />
          </marker>
        </defs>
      </svg>
      <VueFlow
        :id="flowId"
        :nodes="nodes"
        :edges="edges"
        :node-types="nodeTypes"
        :edge-types="edgeTypes"
        :nodes-draggable="false"
        :nodes-connectable="false"
        :elements-selectable="false"
        :edges-focusable="false"
        :min-zoom="0.05"
        :max-zoom="2"
        :only-render-visible-elements="nodeCount > 250"
        :aria-describedby="summaryId"
        @node-click="onNodeClick"
        @node-mouse-enter="({ node }: NodeMouseEvent) => (hoveredId = node.id)"
        @node-mouse-leave="hoveredId = null"
      />
      <div v-if="compact" class="gnote">확대하면 자세히 보여요</div>
    </div>

    <div v-if="groupMembers.length" class="members">
      <span class="muted">접힌 묶음의 멤버</span>
      <ul>
        <li v-for="m in groupMembers" :key="m.id">
          <button type="button" class="btn btn-sm" @click="pickMember(m.firingId)">{{ m.text }}</button>
        </li>
      </ul>
    </div>
  </section>
</template>

<style scoped>
.pg {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 420px;
  background: var(--panel);
  border: 1px solid var(--line);
  border-radius: var(--r);
}
.pg.fixed {
  height: auto;
  min-height: 0;
}
.gtool {
  flex: none;
  display: flex;
  flex-wrap: wrap;
  gap: 6px 12px;
  align-items: center;
  padding: 6px var(--gx);
  border-bottom: 1px solid var(--line);
}
.grp {
  display: flex;
  gap: 4px;
  align-items: center;
}
.btn .ic {
  margin-right: -2px;
}
.zoomv {
  min-width: 3.2em;
  text-align: right;
  font: 600 12px var(--mono);
  color: var(--fg);
}
.opt {
  display: inline-flex;
  gap: 6px;
  align-items: center;
  white-space: nowrap;
}
.gline {
  flex: none;
  padding: 8px var(--gx);
  border-bottom: 1px solid var(--line);
  font-size: 13px;
  color: var(--fg);
}
.gline b {
  font-weight: 600;
  margin-right: 4px;
}
.lgd-wrap {
  flex: none;
  padding: 6px var(--gx);
  border-bottom: 1px solid var(--line);
}
.flow-wrap {
  position: relative;
  flex: 1;
  min-height: 240px;
  background: var(--panel);
}
.pg.fixed .flow-wrap {
  flex: none;
}
.mk-defs {
  position: absolute;
  width: 0;
  height: 0;
  overflow: hidden;
}
.mk-defs path {
  fill: var(--tone);
}
.gnote {
  position: absolute;
  left: 10px;
  bottom: 10px;
  z-index: 5;
  padding: 3px 10px;
  border: 1px solid var(--ctl);
  border-radius: var(--r);
  background: var(--panel);
  color: var(--fg);
  font-size: 12px;
  pointer-events: none;
}
.empty {
  padding: 32px 12px;
  text-align: center;
  color: var(--muted);
}
.members {
  flex: none;
  display: flex;
  flex-wrap: wrap;
  gap: 6px 10px;
  align-items: center;
  padding: 6px var(--gx);
  border-top: 1px solid var(--line);
  font-size: 12px;
}
.members ul {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}
</style>

<style>
/* Vue Flow 노드 래퍼(전역): 포커스 고리는 노드 상자 모양을 따른다. */
.flow-wrap .vue-flow__node:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 3px;
  border-radius: var(--r);
}
.flow-wrap .vue-flow__node {
  cursor: pointer;
}
</style>
