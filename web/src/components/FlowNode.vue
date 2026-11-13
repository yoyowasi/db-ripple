<script setup lang="ts">
// 그래프 노드 7종(목업 v6): 문장 = 진한 알약, EXEC 프로시저 호출 = 초록 알약(--nd-proc, 문장과 같은 계열), 묶음 = 결과 카드, 트리거 = 작은 알약, 오류·잔차 = 빨강 카드, 접힌 묶음 = 묶음 수 카드.
// 색 띠 대신 배경 틴트·기호로 구분한다. 경로 강조·선택·축소 표시는 그래프 맥락(context)을 읽는다.
import { Handle, Position } from '@vue-flow/core'
import { computed } from 'vue'
import type { RippleNodeData } from '../lib/graphModel'
import { useGraphContext } from './graph/context'

defineOptions({ inheritAttrs: false })

const props = defineProps<{
  id: string
  data: RippleNodeData
}>()

const ctx = useGraphContext()
const v = computed(() => props.data.view)
const handleStyle = computed(() => ({ top: `${props.data.boxHeight / 2}px` }))

const cls = computed(() => {
  const d = props.data
  const view = d.view
  const active = ctx.active.value
  return [
    `t-${view.kind}`,
    view.kind === 'firing' ? `op-${view.tone}` : '',
    {
      unobs: view.kind === 'trigger' && !view.observed,
      sel: d.node.type === 'firing' && d.node.firingId === ctx.selectedFiringId.value,
      on: active !== null && active.nodes.has(props.id),
      dim: active !== null && !active.nodes.has(props.id),
    },
  ]
})

const chipText = computed(() => `0행 쓰기 ${props.data.zeroChip}건 · ${ctx.showZeroRows.value ? '접기' : '보기'}`)
</script>

<template>
  <div class="rn" :class="cls" :title="data.sentence">
    <div class="box" :style="{ width: `${data.width}px`, height: `${data.boxHeight}px` }">
      <Handle type="target" :position="Position.Left" :connectable="false" :style="handleStyle" />

      <template v-if="v.kind === 'statement'">
        <svg class="ni" viewBox="0 0 16 16" aria-hidden="true"><path d="M5.5 3L2 8l3.5 5M10.5 3L14 8l-3.5 5" /></svg>
        <div class="txt">
          <div class="l1 mono"><span v-if="v.verb" class="vb">{{ v.verb }}</span> {{ ctx.compact.value ? v.targetShort : v.target }}</div>
          <div class="l2">{{ v.line2 }}</div>
        </div>
      </template>

      <template v-else-if="v.kind === 'procedureCall'">
        <svg class="ni" viewBox="0 0 16 16" aria-hidden="true"><path d="M5 3l8 5-8 5z" /></svg>
        <div class="txt">
          <div class="l1 mono"><span class="vb">EXEC</span> {{ ctx.compact.value ? v.targetShort : v.target }}</div>
          <div class="l2">{{ v.line2 }}</div>
          <div v-if="v.calls" class="l3" :title="`펼친 안쪽 프로시저: ${v.calls}`">
            안쪽 호출 <span class="mono">{{ v.calls }}</span>
          </div>
          <div v-if="v.unobserved" class="l3" :title="`정적으로 쓰는 테이블인데 이번 실행에서 변경이 관측되지 않음: ${v.unobserved}`">
            <span class="obs">관측 안 됨</span> <span class="mono">{{ v.unobserved }}</span>
          </div>
        </div>
      </template>

      <template v-else-if="v.kind === 'firing'">
        <div class="r1">
          <span class="sym" aria-hidden="true">
            <svg viewBox="0 0 16 16">
              <path v-if="v.tone === 'del'" d="M3 8h10" />
              <template v-else-if="v.tone === 'upd'">
                <path d="M3 13l.7-3.2 7.1-7.1 2.5 2.5-7.1 7.1z" />
                <path d="M9.6 4l2.4 2.4" />
              </template>
              <path v-else-if="v.tone === 'add'" d="M8 3v10M3 8h10" />
              <path v-else d="M3.5 6h9M3.5 10h9" />
            </svg>
          </span>
          <span class="res">{{ v.result }}</span>
          <span v-if="v.extra" class="xtra">{{ v.extra }}</span>
          <span v-if="v.keysOnly" class="keys" title="행 이미지 없이 키만 수집함(KEYS)">키만</span>
          <span v-if="data.order !== null" class="num" :title="`화면 표시 순번 ${data.order}`">{{ data.order }}</span>
        </div>
        <div class="r2 mono"><span class="sch">{{ v.schema }}</span><span class="nm">{{ v.name }}</span></div>
      </template>

      <template v-else-if="v.kind === 'trigger'">
        <svg class="ni" viewBox="0 0 16 16" aria-hidden="true"><path d="M9 1.8L3.5 9h4l-.6 5.2L12.5 7h-4z" /></svg>
        <span class="tgn mono">{{ v.name }}</span>
        <span v-if="!v.observed" class="tgs">추정</span>
      </template>

      <template v-else>
        <div class="r1">
          <span class="sym" aria-hidden="true">
            <svg v-if="v.kind === 'group'" viewBox="0 0 16 16"><path d="M3 5h10M3 8h10M3 11h10" /></svg>
            <svg v-else viewBox="0 0 16 16"><circle cx="8" cy="8" r="5.8" /><path d="M4 12L12 4" /></svg>
          </span>
          <span class="res">{{ v.title }}</span>
        </div>
        <div class="sub">{{ v.sub }}</div>
      </template>

      <Handle type="source" :position="Position.Right" :connectable="false" :style="handleStyle" />
    </div>

    <button
      v-if="data.zeroChip"
      type="button"
      class="zchip nodrag nopan"
      :aria-expanded="ctx.showZeroRows.value"
      :aria-label="`0행 쓰기 ${data.zeroChip}건 ${ctx.showZeroRows.value ? '접기' : '보기'}`"
      @click.stop="ctx.toggleZeroRows()"
      @keydown.enter.stop
      @keydown.space.stop
    >
      {{ chipText }}
    </button>
  </div>
</template>

<style scoped>
.rn {
  position: relative;
  color: var(--nd-t);
  cursor: pointer;
  transition: opacity var(--m-out) var(--ease);
}
.box {
  position: relative;
  display: flex;
  align-items: center;
  gap: 8px;
  background: var(--nd);
  border: 1.5px solid var(--nd-s);
  border-radius: var(--r);
  transition:
    border-color var(--m-out) var(--ease),
    box-shadow var(--m-out) var(--ease);
}
.rn:hover .box,
.rn.on .box {
  border-color: var(--accent);
  box-shadow: 0 0 0 1px var(--accent);
}
.rn.sel .box {
  border-color: var(--accent);
  box-shadow: 0 0 0 2px var(--accent);
}
.rn.dim {
  opacity: 0.28;
}
.mono {
  font-family: var(--mono);
}
.ni {
  flex: none;
  width: 16px;
  height: 16px;
  fill: none;
  stroke: currentColor;
  stroke-width: 1.5;
  stroke-linecap: round;
  stroke-linejoin: round;
}

/* 문장: 진한 알약 */
.t-statement .box {
  padding: 0 18px 0 16px;
  background: var(--st-bg);
  border-color: var(--st-s);
  border-radius: 999px;
  color: var(--st-fg);
}
.t-statement .ni {
  color: var(--st-verb);
}
.txt {
  min-width: 0;
  display: grid;
  gap: 1px;
}
.l1 {
  font-size: 13px;
  font-weight: 700;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.vb {
  color: var(--st-verb);
}

/* EXEC 프로시저 호출: 문장 알약과 같은 모양, 색만 v6의 프로시저 토큰(--nd-proc, --k-call). 보조 줄이 있어 키가 더 클 수 있다. */
.t-procedureCall .box {
  padding: 0 18px 0 16px;
  background: var(--nd-proc);
  border: 2px solid var(--nd-proc-s);
  border-radius: 25px;
  color: var(--nd-t);
}
.t-procedureCall .ni {
  color: var(--k-call);
}
.t-procedureCall .vb {
  color: var(--k-call);
}
.t-procedureCall .l2 {
  color: var(--nd-t2);
}
.l3 {
  font-size: 12px;
  line-height: 17px;
  color: var(--nd-t2);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.l3 .mono {
  font-size: 12px;
}
/* 관측 안 됨: 트리거 "추정"과 같은 점선 테두리 */
.obs {
  display: inline-block;
  padding: 0 6px;
  font: 600 12px / 15px var(--ui);
  color: var(--nd-t);
  border: 1px dashed var(--nd-proc-s);
  border-radius: 999px;
}
.l2 {
  font-size: 12px;
  line-height: 1.35;
  color: var(--st-fg2);
  display: -webkit-box;
  -webkit-line-clamp: 2;
  line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
  overflow-wrap: anywhere;
}

/* 묶음·오류·잔차·접힌 묶음: 결과 카드 */
.t-firing .box,
.t-error .box,
.t-residual .box,
.t-group .box {
  flex-direction: column;
  align-items: stretch;
  justify-content: center;
  gap: 4px;
  padding: 0 10px 0 11px;
}
.r1 {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
}
.sym {
  flex: none;
  display: grid;
  place-items: center;
  width: 22px;
  height: 22px;
  border-radius: 50%;
  background: var(--nd-s);
}
.sym svg {
  width: 16px;
  height: 16px;
  fill: none;
  stroke: var(--nd);
  stroke-width: 2;
  stroke-linecap: round;
  stroke-linejoin: round;
}
.res {
  font: 700 16px / 1.2 var(--ui);
  white-space: nowrap;
}
.xtra {
  font-size: 12px;
  color: var(--muted);
  white-space: nowrap;
}
.keys {
  padding: 0 8px;
  font: 600 12px / 18px var(--ui);
  color: var(--warn);
  background: var(--warn-bg);
  border: 1px solid var(--warn);
  border-radius: 999px;
  white-space: nowrap;
}
.num {
  flex: none;
  margin-left: auto;
  display: grid;
  place-items: center;
  min-width: 22px;
  height: 22px;
  padding: 0 4px;
  border: 1px solid var(--nd-s);
  border-radius: 999px;
  background: var(--panel);
  color: var(--fg);
  font: 700 12px var(--mono);
}
.r2,
.sub {
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.sch {
  font-size: 12px;
  color: var(--nd-t2);
}
.nm {
  font-size: 13px;
  font-weight: 700;
}
.sub {
  font-size: 13px;
  color: var(--fg);
}
.op-del .box {
  background: color-mix(in srgb, var(--del-bg) 55%, var(--nd));
  border-color: var(--del-fg);
}
.op-del .res {
  color: var(--del-fg);
}
.op-del .sym {
  background: var(--del-fg);
}
.op-upd .box {
  background: color-mix(in srgb, var(--chg-bg) 55%, var(--nd));
  border-color: var(--warn);
}
.op-upd .res {
  color: var(--warn);
}
.op-upd .sym {
  background: var(--warn);
}
.op-add .box {
  background: color-mix(in srgb, var(--add-bg) 55%, var(--nd));
  border-color: var(--add-fg);
}
.op-add .res {
  color: var(--add-fg);
}
.op-add .sym {
  background: var(--add-fg);
}
.op-zero .box {
  background: var(--sunk);
}
.op-zero .res {
  color: var(--muted);
}
.op-zero .sym {
  background: var(--muted);
}
.op-zero .nm {
  color: var(--muted);
}
.t-error .box,
.t-residual .box {
  background: var(--err-bg);
  border-color: var(--err);
}
.t-residual .box {
  border-style: dashed;
}
.t-error .res,
.t-residual .res {
  color: var(--err);
}
.t-error .sym,
.t-residual .sym {
  background: var(--err);
}

/* 트리거: 작은 알약 */
.t-trigger .box {
  padding: 0 14px 0 12px;
  background: var(--nd-trig);
  border-color: var(--k-fires);
  border-radius: 999px;
}
.t-trigger.unobs .box {
  border-style: dashed;
}
.t-trigger .ni {
  color: var(--k-fires);
}
.tgn {
  font-size: 12px;
  font-weight: 700;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.tgs {
  font-size: 12px;
  color: var(--muted);
  white-space: nowrap;
}

/* 0행 칩 */
.zchip {
  position: absolute;
  left: 0;
  top: calc(100% + 6px);
  height: 26px;
  padding: 0 12px;
  border: 1px solid var(--ctl);
  border-radius: 999px;
  background: var(--panel);
  color: var(--fg);
  font: 600 12px var(--ui);
  white-space: nowrap;
  cursor: pointer;
}
.zchip:hover,
.zchip:focus-visible {
  border-color: var(--accent);
  background: color-mix(in srgb, var(--accent) 10%, var(--panel));
}

:deep(.vue-flow__handle) {
  width: 1px;
  height: 1px;
  min-width: 0;
  min-height: 0;
  background: transparent;
  border: none;
}
</style>

<style>
/* 배율 80% 미만: 부가 줄·순번·문장 둘째 줄을 숨기고 결과 글자를 키운다(.lo는 PropagationGraph가 붙인다). 이름은 폭 어림이 깨지지 않게 그대로 둔다. */
.lo .rn .xtra,
.lo .rn .sch,
.lo .rn .num,
.lo .rn .l2,
.lo .rn .l3,
.lo .rn .sub,
.lo .rn .tgs {
  display: none;
}
.lo .rn .res {
  font-size: 17px;
}
</style>
