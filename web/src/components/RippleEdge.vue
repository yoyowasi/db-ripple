<script setup lang="ts">
// 간선: 확신도 → 선 모양, 종류 → 색 + 라벨 글자, observed:false 목표 → 흐린 점선.
import { BaseEdge, EdgeLabelRenderer, getBezierPath } from '@vue-flow/core'
import type { Position } from '@vue-flow/core'
import { computed } from 'vue'
import type { RippleEdgeData } from '../lib/graphModel'
import { CONFIDENCE_STYLE, KIND_COLOR, UNOBSERVED_DASH } from '../lib/graphStyle'

defineOptions({ inheritAttrs: false })

const props = defineProps<{
  id: string
  sourceX: number
  sourceY: number
  targetX: number
  targetY: number
  sourcePosition: string
  targetPosition: string
  data?: RippleEdgeData
  markerEnd?: string
  selected?: boolean
}>()

const path = computed(() =>
  getBezierPath({
    sourceX: props.sourceX,
    sourceY: props.sourceY,
    sourcePosition: props.sourcePosition as Position,
    targetX: props.targetX,
    targetY: props.targetY,
    targetPosition: props.targetPosition as Position,
  }),
)

const style = computed(() => {
  const e = props.data?.edge
  if (!e) return {}
  const c = CONFIDENCE_STYLE[e.confidence]
  const unobserved = props.data?.toUnobserved ?? false
  const color = e.confidence === 'unexplained' ? '#d4a017' : KIND_COLOR[e.kind]
  return {
    stroke: color,
    strokeWidth: props.selected ? c.width + 1.5 : c.width,
    strokeDasharray: unobserved ? UNOBSERVED_DASH : c.dash,
    strokeLinecap: unobserved ? ('round' as const) : ('butt' as const),
    opacity: unobserved ? 0.6 : 1,
  }
})

const labelStyle = computed(() => ({
  transform: `translate(-50%, -50%) translate(${path.value[1]}px, ${path.value[2]}px)`,
}))
</script>

<template>
  <BaseEdge :id="id" :path="path[0]" :marker-end="markerEnd" :style="style" />
  <EdgeLabelRenderer>
    <div v-if="data?.labelText" class="edge-label nodrag nopan" :style="labelStyle">
      {{ data.labelText }}
    </div>
  </EdgeLabelRenderer>
</template>

<style>
/* EdgeLabelRenderer 는 Vue Flow 컨테이너로 옮겨 그리므로 scoped 가 아닌 전역 클래스로 둔다. */
.edge-label {
  position: absolute;
  pointer-events: none;
  padding: 0 5px;
  font-size: 10.5px;
  line-height: 16px;
  white-space: nowrap;
  border-radius: 4px;
  background: color-mix(in srgb, var(--bg-elev) 88%, transparent);
  color: var(--fg-muted);
  border: 1px solid var(--border);
}
.hide-edge-labels .edge-label {
  display: none;
}
</style>
