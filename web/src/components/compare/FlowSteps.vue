<script setup lang="ts">
// 전파 단계 칩(리디자인 시안 2026-11-23): 문장 → 직접 → FK 연쇄 → 트리거를 칩 한 줄로. 긴 한 줄 요약 문장을 대신한다.
// 숫자는 발동 기준(묶음 rowCount 합)이라 같은 행을 여러 번 바꾸면 겹쳐 센다. 순변경 합계는 아래 원인 트리의 합계 줄이 말한다.
import { computed } from 'vue'
import type { PreviewResult } from '../../api/types'
import { formatCount } from '../../lib/format'
import { CAUSE_LABEL, causeBreakdown } from '../../lib/summary'
import type { CauseKind } from '../../lib/summary'

const props = defineProps<{ preview: PreviewResult }>()

type Tone = 'd' | 'f' | 't' | 'x'
const TONE: Record<CauseKind, Tone> = {
  DIRECT: 'd',
  PROC_DIRECT: 'd',
  FK_CASCADE: 'f',
  FK_SET_NULL: 'f',
  FK_SET_DEFAULT: 'f',
  INSTEAD_OF: 't',
  TRIGGER: 't',
  UNEXPLAINED: 'x',
}

/** 첫 칩: 문장 종류. 여러 문장이면 "문장 2 · UPDATE·DELETE" */
const head = computed(() => {
  const st = props.preview.statements
  const kinds = [...new Set(st.map((s) => s.kind))].join('·')
  return st.length > 1 ? `문장 ${st.length} · ${kinds}` : kinds || '문장'
})

const steps = computed(() => {
  const b = causeBreakdown(props.preview.firings)
  const zero = new Map<CauseKind, number>()
  for (const f of b.zeroRow) zero.set(f.cause.kind, (zero.get(f.cause.kind) ?? 0) + 1)
  const out = b.groups.map((g) => {
    const z = zero.get(g.kind) ?? 0
    zero.delete(g.kind)
    return { key: g.kind, tone: TONE[g.kind], label: g.label, value: `${formatCount(g.rows)}행`, rest: `${formatCount(g.tables)}개 테이블${z ? ` · 0행 쓰기 ${formatCount(z)}건` : ''}` }
  })
  // 행을 바꾸지 않은 원인도 흐름에서 빠지지 않게(예: 트리거가 0행 쓰기만 했다)
  for (const [k, z] of zero) out.push({ key: k, tone: TONE[k], label: `${CAUSE_LABEL[k]} 쓰기`, value: '0행 변경', rest: `${formatCount(z)}건` })
  return out
})
</script>

<template>
  <ol class="flow" aria-label="전파 요약. 원인 종류별 발동 기준 행 수입니다(순변경 합계는 아래 표)">
    <li class="step s0"><span class="mono">{{ head }}</span></li>
    <template v-for="(s, i) in steps" :key="s.key">
      <li class="arrow" aria-hidden="true" :style="{ '--i': i + 1 }">→</li>
      <li class="step" :class="s.tone" :style="{ '--i': i + 1 }">
        {{ s.label }} <b>{{ s.value }}</b> <span class="rest">· {{ s.rest }}</span>
      </li>
    </template>
    <li v-if="!steps.length" class="step none" :style="{ '--i': 1 }">관측된 변경 없음</li>
  </ol>
</template>

<style scoped>
.flow {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
  margin: 0;
  padding: 0;
  list-style: none;
}
.step {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 3px 11px;
  border: 1px solid var(--line);
  border-radius: 99px;
  background: var(--panel);
  font-size: 13px;
  white-space: nowrap;
}
.step b {
  font-weight: 600;
}
.s0 .mono {
  font-size: 12px;
  font-weight: 600;
}
.rest {
  color: var(--muted);
}
.d b {
  color: var(--err);
}
.f b {
  color: var(--k-fk);
}
.t b {
  color: var(--warn);
}
.x {
  border-style: dashed;
  border-color: var(--err);
}
.x b {
  color: var(--err);
}
.none {
  color: var(--muted);
}
.arrow {
  color: var(--muted);
}
@media (prefers-reduced-motion: no-preference) {
  .step,
  .arrow {
    animation: dr-pop 0.35s var(--ease) both;
    animation-delay: calc(var(--i, 0) * 0.12s);
  }
}
</style>
