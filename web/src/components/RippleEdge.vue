<script setup lang="ts">
// 간선: 선 모양 = 확신도, 색 = 종류, 가운데 라벨 알약(축소 표시면 짧게). 자세한 근거는 title.
import { EdgeLabelRenderer, getBezierPath } from '@vue-flow/core'
import type { Position } from '@vue-flow/core'
import { computed } from 'vue'
import type { RippleEdgeData } from '../lib/graphModel'
import { EDGE_TONE } from '../lib/graphStyle'
import { useGraphContext } from './graph/context'

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
}>()

const ctx = useGraphContext()

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

const tone = computed(() => (props.data ? EDGE_TONE[props.data.edge.kind] : 'direct'))
const state = computed(() => {
  const a = ctx.active.value
  return { on: a !== null && a.edges.has(props.id), dim: a !== null && !a.edges.has(props.id) }
})
const delay = computed(() => ({ '--dl': `${(((props.data?.rank ?? 1) - 1) * ctx.drawStep.value).toFixed(2)}s` }))
const labelStyle = computed(() => ({
  ...delay.value,
  transform: `translate(-50%, -50%) translate(${path.value[1]}px, ${path.value[2]}px)`,
}))
</script>

<template>
  <g :class="state">
    <path class="rhit" :d="path[0]" />
    <path
      class="rpath"
      :class="[`tone-${tone}`, `c-${data?.edge.confidence ?? 'certain'}`]"
      :d="path[0]"
      :style="delay"
      :pathLength="ctx.drawing.value ? 1 : undefined"
      :marker-end="ctx.drawing.value ? undefined : `url(#${ctx.markerId(tone)})`"
    >
      <title>{{ data?.title }}</title>
    </path>
  </g>
  <EdgeLabelRenderer>
    <div
      v-if="data"
      class="rlabel nodrag nopan"
      :class="[`tone-${tone}`, { inf: data.edge.confidence !== 'certain', ...state }]"
      :style="labelStyle"
      :title="data.title"
    >
      {{ ctx.compact.value ? data.short : data.label }}
    </div>
  </EdgeLabelRenderer>
</template>

<style>
/* 라벨은 Vue Flow가 컴포넌트 밖 레이어에 옮겨 그리므로 전역 규칙으로 둔다. */
.rhit {
  fill: none;
  stroke: transparent;
  stroke-width: 14;
}
.rpath {
  fill: none;
  stroke-width: 2;
  transition:
    stroke-width var(--m-out) var(--ease),
    opacity var(--m-out) var(--ease);
}
.rpath.c-inferred {
  stroke-dasharray: 8 5;
}
.rpath.c-ambiguous,
.rpath.c-unexplained {
  stroke-dasharray: 2 5;
  stroke-linecap: round;
}
.tone-direct {
  --tone: var(--k-direct);
}
.tone-call {
  --tone: var(--k-call);
}
.tone-fk {
  --tone: var(--k-fk);
}
.tone-fires {
  --tone: var(--k-fires);
}
.tone-blocked {
  --tone: var(--k-blocked);
}
.tone-residual {
  --tone: var(--err);
}
.rpath {
  stroke: var(--tone);
}
.rpath.tone-blocked {
  stroke-width: 2.6;
}
.on > .rpath {
  stroke-width: 3;
}
.dim > .rpath {
  opacity: 0.28;
}

.rlabel {
  position: absolute;
  pointer-events: all;
  padding: 0 8px;
  font: 600 12px / 20px var(--ui);
  white-space: nowrap;
  border-radius: 999px;
  border: 1.3px solid var(--tone);
  background: var(--panel);
  color: var(--tone);
  cursor: default;
  transition: opacity var(--m-out) var(--ease);
}
.rlabel.inf {
  border-style: dashed;
}
.rlabel.dim {
  opacity: 0.28;
}
.lo .rlabel {
  font-size: 14px;
  line-height: 22px;
}

@media (prefers-reduced-motion: no-preference) {
  .drawing .rpath {
    stroke-dasharray: 1;
    stroke-dashoffset: 1;
    animation: rg-edraw 0.28s var(--ease) var(--dl) forwards;
  }
  .drawing .rlabel {
    opacity: 0;
    animation: rg-efade 0.16s linear calc(var(--dl) + 0.2s) forwards;
  }
}
@keyframes rg-edraw {
  to {
    stroke-dashoffset: 0;
  }
}
@keyframes rg-efade {
  to {
    opacity: 1;
  }
}
</style>
