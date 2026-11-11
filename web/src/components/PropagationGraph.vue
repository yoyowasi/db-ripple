<script setup lang="ts">
// C2 전파 그래프. 노드 6종·간선 7종, dagre 배치(왼쪽 → 오른쪽), 확신도별 선 모양.
// 묶음(firing) 노드를 누르면 그리드가 그 묶음으로 걸러진다. 키보드로는 그리드 위의 "묶음" 선택 목록을 쓴다.
import { VueFlow, useVueFlow } from '@vue-flow/core'
import type { EdgeMouseEvent, NodeMouseEvent } from '@vue-flow/core'
import { computed, markRaw, nextTick, ref, shallowRef, watch } from 'vue'
import type { Firing, GraphDto, GraphEdge } from '../api/types'
import { CONFIDENCE_LABEL } from '../lib/format'
import { buildFlow, describeNode } from '../lib/graphModel'
import type { FlowModel, RippleNodeData } from '../lib/graphModel'
import { KIND_DESCRIPTION, KIND_LABEL, OBSERVED_ONLY_TEXT } from '../lib/graphStyle'
import FlowNode from './FlowNode.vue'
import GraphLegend from './GraphLegend.vue'
import RippleEdge from './RippleEdge.vue'

const props = withDefaults(
  defineProps<{
    graph: GraphDto | null
    firings: Firing[]
    selectedFiringId: string | null
    showZeroRows: boolean
    height?: number
    flowId?: string
  }>(),
  { height: 440, flowId: 'ripple-graph' },
)

const emit = defineEmits<{
  'select-firing': [firingId: string]
  'update:showZeroRows': [value: boolean]
  /** 배치(dagre)가 끝났다 */
  'layout-done': [stats: { layoutMs: number; nodes: number; edges: number }]
  /** 배치 뒤 두 프레임이 지나 화면에 그려졌다(SP9 측정용) */
  painted: []
}>()

// markRaw: 컴포넌트 정의를 반응형으로 만들지 않는다.
const nodeTypes = { ripple: markRaw(FlowNode) } as never
const edgeTypes = { ripple: markRaw(RippleEdge) } as never

const { fitView, zoomIn, zoomOut } = useVueFlow({ id: props.flowId })

// 선택한 노드·간선의 상세(아래 줄에 글로 보인다)
type Detail = { kind: 'node'; id: string } | { kind: 'edge'; id: string }
const detail = ref<Detail | null>(null)

const showLabels = ref(true)
const model = shallowRef<FlowModel | null>(null)

watch(
  () => [props.graph, props.firings, props.showZeroRows] as const,
  async ([graph, firings, showZero]) => {
    if (!graph) {
      model.value = null
      return
    }
    const m = buildFlow(graph, firings, showZero)
    model.value = m
    detail.value = null
    emit('layout-done', { layoutMs: m.layoutMs, nodes: m.nodes.length, edges: m.edges.length })
    await nextTick()
    requestAnimationFrame(() =>
      requestAnimationFrame(() => {
        void fitView({ padding: 0.12 })
        emit('painted')
      }),
    )
  },
  { immediate: true },
)

// 선택한 묶음 노드에 강조 클래스를 붙인다(배치는 다시 하지 않는다).
const displayNodes = computed(() =>
  (model.value?.nodes ?? []).map((n) => {
    const d = n.data
    const on = !!d && d.node.type === 'firing' && d.node.firingId === props.selectedFiringId
    return on ? { ...n, class: 'is-active' } : n
  }),
)
const displayEdges = computed(() => model.value?.edges ?? [])

const detailNode = computed<RippleNodeData | null>(() => {
  const d = detail.value
  if (!d || d.kind !== 'node') return null
  return model.value?.nodes.find((n) => n.id === d.id)?.data ?? null
})
const detailEdge = computed<GraphEdge | null>(() => {
  const d = detail.value
  if (!d || d.kind !== 'edge') return null
  return model.value?.edges.find((e) => e.id === d.id)?.data?.edge ?? null
})

function onNodeClick({ node }: NodeMouseEvent): void {
  detail.value = { kind: 'node', id: node.id }
  const data = node.data as RippleNodeData | undefined
  if (data?.node.type === 'firing') emit('select-firing', data.node.firingId)
}
function onEdgeClick({ edge }: EdgeMouseEvent): void {
  detail.value = { kind: 'edge', id: edge.id }
}

/** 접힌 묶음의 멤버 id → 걸러낼 firingId(노드를 찾으면 그 firingId, 못 찾으면 id 그대로) */
function memberFiringId(memberId: string): string {
  const n = props.graph?.nodes.find((x) => x.id === memberId)
  return n && n.type === 'firing' ? n.firingId : memberId
}

const hasGroup = computed(() => props.graph?.nodes.some((n) => n.type === 'group') ?? false)
const nodeCount = computed(() => model.value?.nodes.length ?? 0)
</script>

<template>
  <section class="card graph-card" aria-label="전파 그래프">
    <header>
      <h2>전파 그래프</h2>
      <label class="opt">
        <input
          type="checkbox"
          :checked="showZeroRows"
          @change="emit('update:showZeroRows', ($event.target as HTMLInputElement).checked)"
        />
        0행 묶음 보기
        <span v-if="model && !showZeroRows && model.hiddenZeroRows > 0" class="muted">({{ model.hiddenZeroRows }}개 숨김)</span>
      </label>
      <label class="opt">
        <input v-model="showLabels" type="checkbox" />
        간선 라벨
      </label>
      <span class="zoom">
        <button type="button" class="btn btn-sm" aria-label="축소" @click="zoomOut()">−</button>
        <button type="button" class="btn btn-sm" aria-label="확대" @click="zoomIn()">+</button>
        <button type="button" class="btn btn-sm" @click="fitView({ padding: 0.12 })">화면에 맞춤</button>
      </span>
    </header>

    <div v-if="!graph" class="empty">미리보기를 실행하면 전파 그래프가 여기에 나옵니다.</div>
    <div v-else-if="nodeCount === 0" class="empty">그릴 노드가 없습니다.</div>
    <div v-else class="flow-wrap" :class="{ 'hide-edge-labels': !showLabels }" :style="{ height: `${height}px` }">
      <VueFlow
        :id="flowId"
        :nodes="displayNodes"
        :edges="displayEdges"
        :node-types="nodeTypes"
        :edge-types="edgeTypes"
        :nodes-draggable="false"
        :nodes-connectable="false"
        :min-zoom="0.05"
        :max-zoom="2"
        :only-render-visible-elements="nodeCount > 250"
        :elevate-edges-on-select="true"
        @node-click="onNodeClick"
        @edge-click="onEdgeClick"
        @pane-click="detail = null"
      />
    </div>

    <p v-if="hasGroup" class="note muted">
      노드가 200개를 넘어 서버가 같은 테이블·연산·깊이의 묶음을 하나로 접었습니다. 접힌 노드를 누르면 멤버를 고를 수 있습니다.
    </p>

    <!-- 상세: 눌러서 본 노드·간선을 글로 보인다 -->
    <div class="detail" aria-live="polite">
      <template v-if="detailNode">
        <strong>{{ describeNode(detailNode.node) }}</strong>
        <template v-if="detailNode.node.type === 'group'">
          <ul class="members">
            <li v-for="m in detailNode.node.members" :key="m">
              <code>{{ m }}</code>
              <button type="button" class="btn btn-sm" @click="emit('select-firing', memberFiringId(m))">이 묶음으로 걸러 보기</button>
            </li>
          </ul>
        </template>
        <template v-if="detailNode.firing">
          <dl class="kv">
            <dt>원인</dt>
            <dd>
              {{ detailNode.firing.cause.kind }} · {{ CONFIDENCE_LABEL[detailNode.firing.cause.confidence] }}
              <template v-if="detailNode.firing.cause.foreignKey">
                · FK {{ detailNode.firing.cause.foreignKey.name }} ({{ detailNode.firing.cause.foreignKey.action }}, 일치 {{ detailNode.firing.cause.foreignKey.matchedRows }}행)
              </template>
            </dd>
            <dt v-if="detailNode.firing.cause.triggerCandidates.length">트리거 후보</dt>
            <dd v-if="detailNode.firing.cause.triggerCandidates.length">{{ detailNode.firing.cause.triggerCandidates.join(', ') }}</dd>
            <dt>근거</dt>
            <dd>{{ detailNode.firing.cause.evidence.join(', ') || '-' }}</dd>
            <dt>형제 순서</dt>
            <dd>{{ detailNode.firing.orderGuarantee === 'observed_only' ? OBSERVED_ONLY_TEXT : '보장됨' }}</dd>
          </dl>
        </template>
      </template>
      <template v-else-if="detailEdge">
        <strong>{{ KIND_LABEL[detailEdge.kind] }} 간선</strong>
        <span class="muted"> {{ KIND_DESCRIPTION[detailEdge.kind] }}</span>
        <dl class="kv">
          <dt>확신도</dt>
          <dd>{{ CONFIDENCE_LABEL[detailEdge.confidence] }}</dd>
          <dt>순서</dt>
          <dd>{{ detailEdge.orderGuarantee === 'observed_only' ? OBSERVED_ONLY_TEXT : '보장됨' }}</dd>
          <dt>근거</dt>
          <dd>{{ detailEdge.evidence.join(', ') || '-' }}</dd>
          <template v-if="detailEdge.fk">
            <dt>FK</dt>
            <dd>{{ detailEdge.fk }} · {{ detailEdge.action }} · {{ detailEdge.matchedRows ?? '?' }}행</dd>
          </template>
        </dl>
      </template>
      <span v-else class="muted">노드나 간선을 누르면 설명이 여기에 보입니다. 묶음 노드를 누르면 아래 그리드가 그 묶음으로 걸러집니다.</span>
    </div>

    <div class="legend-wrap"><GraphLegend /></div>
  </section>
</template>

<style scoped>
.graph-card {
  display: flex;
  flex-direction: column;
}
.opt {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  font-size: 12.5px;
}
.zoom {
  display: inline-flex;
  gap: 4px;
}
.flow-wrap {
  width: 100%;
  background: var(--bg-sunken);
}
.empty {
  padding: 32px 12px;
  text-align: center;
  color: var(--fg-muted);
}
.note {
  margin: 0;
  padding: 6px 12px;
  font-size: 12px;
}
.detail {
  padding: 8px 12px;
  border-top: 1px solid var(--border);
  font-size: 12.5px;
  min-height: 40px;
}
.members {
  list-style: none;
  margin: 4px 0 0;
  padding: 0;
  display: flex;
  flex-wrap: wrap;
  gap: 4px 12px;
}
.members li {
  display: inline-flex;
  align-items: center;
  gap: 6px;
}
.legend-wrap {
  padding: 4px 12px 8px;
}
</style>

<style>
/* Vue Flow 노드 래퍼는 이 컴포넌트 밖(전역)에 그려지므로 전역 규칙으로 강조한다. */
.vue-flow__node.is-active .rnode {
  outline: 2px solid var(--accent);
  outline-offset: 1px;
}
.vue-flow__node:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}
.vue-flow__edge-path {
  transition: stroke-width 0.1s;
}
</style>
