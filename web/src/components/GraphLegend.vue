<script setup lang="ts">
// 범례(목업 v6): 상시 한 줄(선 모양 + 번호 뜻) + 접이식 "읽는 법". 그 그래프에 있는 종류만 보인다.
import type { LegendPresence } from '../lib/graphModel'
import { CONFIDENCE_LINE, TONE_LEGEND } from '../lib/graphStyle'

defineProps<{ present: LegendPresence }>()

const DASH: Record<string, string | undefined> = { certain: undefined, inferred: '8 5', ambiguous: '2 5', unexplained: '2 5' }
const OP_TEXT = {
  del: '빨강 카드 · 빼기 = 행 삭제',
  upd: '호박색 카드 · 연필 = 행 수정',
  add: '초록 카드 · 더하기 = 행 추가',
  zero: '회색 흐린 카드 = 변경 없음(0행). 발동했지만 바뀐 행이 없습니다',
} as const
</script>

<template>
  <div class="lgd">
    <div class="row">
      <span v-if="present.confidences.length" class="item">
        선 모양:
        <template v-for="(c, i) in present.confidences" :key="c">
          <span v-if="i > 0" aria-hidden="true">·</span>
          <svg class="lg" viewBox="0 0 36 10" aria-hidden="true">
            <path d="M1 5H35" :stroke-dasharray="DASH[c]" :stroke-linecap="c === 'certain' || c === 'inferred' ? 'butt' : 'round'" />
          </svg>
          {{ CONFIDENCE_LINE[c] }}
        </template>
      </span>
      <span v-if="present.hasOrder" class="item ord">번호 = 화면 표시 순번(실행 순서 아님 · 관측 순서, 보장 안 됨)</span>
    </div>
    <details class="howto">
      <summary>읽는 법</summary>
      <ul>
        <li v-if="present.statement"><i class="sw sw-st" />진한 알약 = 출발점(문장)</li>
        <li v-for="o in present.ops" :key="o"><i class="sw" :class="`sw-${o}`" />{{ OP_TEXT[o] }}</li>
        <li v-if="present.keysOnly"><i class="sw sw-keys" />"키만" = 행 값 없이 키만 수집함(KEYS)</li>
        <li v-if="present.error"><i class="sw sw-err" />빨강 카드 · 금지 기호 = 문장이 실패하거나 트리거가 거부한 오류</li>
        <li v-if="present.residual"><i class="sw sw-err d" />빨강 점선 카드 = 설명되지 않은 변경(원인을 못 찾음)</li>
        <li v-if="present.group"><i class="sw sw-zero" />묶음 수 카드 = 노드가 많아 같은 테이블·연산·깊이 묶음을 접음</li>
        <li v-if="present.trigger"><i class="sw sw-tg" />번개 알약 = 트리거. 실선 테두리 = 관측됨</li>
        <li v-if="present.unobserved"><i class="sw sw-tg d" />점선 테두리 + "추정" = 관측 안 됨. 연결은 카탈로그로 추정했습니다</li>
        <li v-for="t in present.tones" :key="t">
          <i class="sw-l" :class="`tone-${t}`" />
          <span><b :class="`tone-${t}`">{{ TONE_LEGEND[t][0] }}</b> 간선 = {{ TONE_LEGEND[t][1] }}</span>
        </li>
      </ul>
    </details>
  </div>
</template>

<style scoped>
.lgd {
  font-size: 12px;
  color: var(--muted);
}
.row {
  display: flex;
  flex-wrap: wrap;
  gap: 4px 18px;
  align-items: center;
}
.item {
  display: inline-flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 5px;
}
.ord {
  color: var(--fg);
  font-weight: 600;
}
.lg {
  width: 30px;
  height: 10px;
}
.lg path {
  fill: none;
  stroke: var(--k-direct);
  stroke-width: 2.2;
}
.howto > summary {
  display: inline-block;
  margin-top: 2px;
  color: var(--accent);
  text-decoration: underline;
  text-underline-offset: 3px;
  cursor: pointer;
}
.howto ul {
  list-style: none;
  margin: 6px 0 2px;
  padding: 0;
  display: grid;
  gap: 4px;
  color: var(--fg);
}
.howto li {
  display: flex;
  gap: 8px;
  align-items: center;
}
.sw {
  display: inline-block;
  flex: none;
  width: 14px;
  height: 14px;
  border-radius: 50%;
}
.sw-del {
  background: var(--del-fg);
}
.sw-upd {
  background: var(--warn);
}
.sw-add {
  background: var(--add-fg);
}
.sw-zero {
  background: var(--muted);
}
.sw-keys {
  width: 22px;
  border-radius: 7px;
  background: var(--warn-bg);
  border: 1px solid var(--warn);
}
.sw-err {
  border-radius: var(--r);
  background: var(--err-bg);
  border: 1.5px solid var(--err);
}
.sw-err.d {
  border-style: dashed;
}
.sw-st {
  width: 22px;
  border-radius: 7px;
  background: var(--st-bg);
  border: 1px solid var(--st-s);
}
.sw-tg {
  width: 22px;
  border-radius: 7px;
  background: var(--nd-trig);
  border: 1.6px solid var(--k-fires);
}
.sw-tg.d {
  border-style: dashed;
}
.sw-l {
  display: inline-block;
  flex: none;
  width: 22px;
  height: 4px;
  border-radius: 2px;
  background: var(--tone);
}
b[class^='tone-'] {
  color: var(--tone);
}
</style>
