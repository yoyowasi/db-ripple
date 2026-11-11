<script setup lang="ts">
import type { Confidence, GraphEdgeKind } from '../api/types'
import { CONFIDENCE_STYLE, KIND_COLOR, KIND_DESCRIPTION, KIND_LABEL, OBSERVED_ONLY_TEXT, UNOBSERVED_DASH } from '../lib/graphStyle'

const confidences: Confidence[] = ['certain', 'inferred', 'ambiguous', 'unexplained']
const kinds: GraphEdgeKind[] = ['DIRECT', 'FK', 'FIRES', 'REPLACES', 'WRITES', 'RESIDUAL', 'BLOCKED']
</script>

<template>
  <details class="legend">
    <summary>범례</summary>
    <div class="cols">
      <section>
        <h3>선 모양 = 확신도</h3>
        <ul>
          <li v-for="c in confidences" :key="c">
            <svg width="44" height="10" aria-hidden="true">
              <line
                x1="0"
                y1="5"
                x2="44"
                y2="5"
                :stroke="c === 'unexplained' ? '#d4a017' : 'currentColor'"
                :stroke-width="CONFIDENCE_STYLE[c].width"
                :stroke-dasharray="CONFIDENCE_STYLE[c].dash"
              />
            </svg>
            <span>{{ CONFIDENCE_STYLE[c].description }} ({{ CONFIDENCE_STYLE[c].label }})</span>
          </li>
          <li>
            <svg width="44" height="10" aria-hidden="true">
              <line x1="0" y1="5" x2="44" y2="5" stroke="currentColor" stroke-width="2" :stroke-dasharray="UNOBSERVED_DASH" stroke-linecap="round" opacity="0.6" />
            </svg>
            <span>흐린 점(관측 안 됨): 변경을 남기지 않은 트리거를 카탈로그로 추정한 발동</span>
          </li>
        </ul>
      </section>
      <section>
        <h3>색·라벨 = 간선 종류</h3>
        <ul>
          <li v-for="k in kinds" :key="k">
            <svg width="44" height="10" aria-hidden="true">
              <line x1="0" y1="5" x2="44" y2="5" :stroke="KIND_COLOR[k]" stroke-width="3" />
            </svg>
            <span><strong>{{ KIND_LABEL[k] }}</strong> {{ KIND_DESCRIPTION[k] }}</span>
          </li>
        </ul>
      </section>
      <section>
        <h3>노드 표시</h3>
        <ul>
          <li><span class="chip chip-info">0행</span> 영향 행이 0개인 묶음(기본 숨김)</li>
          <li><span class="chip chip-warn">일부만 수집</span> 키만 수집(imageMode KEYS)</li>
          <li><span class="chip chip-warn">감사 순서 불확실</span> auditOrder NONE</li>
          <li><span class="chip">관측 안 됨(추정)</span> 점선 테두리 트리거</li>
          <li>간선 라벨의 "{{ OBSERVED_ONLY_TEXT }}": 형제 묶음 사이 순서는 관측일 뿐 문서로 보장되지 않음</li>
        </ul>
      </section>
    </div>
  </details>
</template>

<style scoped>
.legend {
  font-size: 12px;
  color: var(--fg-muted);
}
summary {
  cursor: pointer;
  width: max-content;
}
.cols {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(280px, 1fr));
  gap: 8px 24px;
  padding-top: 6px;
}
ul {
  list-style: none;
  margin: 4px 0 0;
  padding: 0;
  display: grid;
  gap: 3px;
}
li {
  display: flex;
  align-items: center;
  gap: 8px;
}
svg {
  flex: none;
  color: var(--fg);
}
</style>
