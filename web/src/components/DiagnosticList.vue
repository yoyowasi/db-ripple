<script setup lang="ts">
// 진단 줄: 거부(error)·경고(warning)를 먼저, statements 를 주면 거부 없는 문장의 "통과" 줄을 뒤에. 규칙 ID·위치·메시지·설계 참조.
import { computed } from 'vue'
import type { AnalyzedStatement, Diagnostic } from '../api/types'
import { spanText, statementTarget } from '../lib/format'

const props = defineProps<{
  items: Diagnostic[]
  /** 분석된 문장. 주면 거부가 없는 문장마다 "통과" 줄을 뒤에 붙인다. */
  statements?: AnalyzedStatement[]
  emptyText?: string
}>()

const passed = computed(() =>
  (props.statements ?? [])
    .filter((s) => !props.items.some((d) => d.severity === 'error' && d.statementIndex === s.index))
    .map((s) => ({
      index: s.index,
      text: `${s.kind === 'EXEC' ? '' : `${s.kind} `}${statementTarget(s)} · 경고 ${props.items.filter((d) => d.severity === 'warning' && d.statementIndex === s.index).length}건`,
      nondet: s.nondeterministicColumns,
    })),
)
</script>

<template>
  <p v-if="items.length === 0 && passed.length === 0" class="empty">{{ emptyText ?? '없음' }}</p>
  <ul v-else class="diag">
    <li v-for="(d, i) in items" :key="`${d.ruleId}-${i}`" :class="d.severity === 'error' ? 'e' : 'w'">
      <span class="t">
        <svg v-if="d.severity === 'error'" class="ic" viewBox="0 0 16 16" aria-hidden="true">
          <circle cx="8" cy="8" r="5.8" />
          <path d="M4 12L12 4" />
        </svg>
        <svg v-else class="ic" viewBox="0 0 16 16" aria-hidden="true">
          <path d="M8 2.2L14.2 13H1.8z" />
          <path d="M8 6.6v3M8 11.4v.1" />
        </svg>
        {{ d.severity === 'error' ? '거부' : '경고' }}
      </span>
      <span class="ln">
        <template v-if="d.statementIndex !== null">문장 {{ d.statementIndex }} · </template>{{ spanText(d.span) }}
      </span>
      <span class="msg">
        <code>{{ d.ruleId }}</code> {{ d.message }}
        <span v-if="d.docRef" class="muted"> (설계 {{ d.docRef }})</span>
      </span>
    </li>
    <li v-for="s in passed" :key="`s${s.index}`" class="i">
      <span class="t">
        <svg class="ic" viewBox="0 0 16 16" aria-hidden="true"><path d="M3 8.5l3.2 3.2L13 4.8" /></svg>통과
      </span>
      <span class="ln">문장 {{ s.index }}</span>
      <span>
        {{ s.text }}
        <span v-if="s.nondet.length" class="muted" title="문장 안 비결정 함수 등으로 값이 달라질 수 있는 열"> · 예상값 열: {{ s.nondet.join(', ') }}</span>
      </span>
    </li>
  </ul>
</template>

<style scoped>
.empty {
  margin: 0;
  color: var(--muted);
}
.diag {
  margin: 0;
  padding: 0;
  list-style: none;
  font-size: 13px;
  /* 오른쪽 레일처럼 좁은 곳에서는 메시지를 아래 줄로 내린다(뷰포트가 아니라 이 목록의 폭 기준). */
  container-type: inline-size;
}
li {
  display: grid;
  grid-template-columns: 72px 120px minmax(0, 1fr);
  gap: 8px;
  align-items: start;
  padding: 3px var(--gx);
  border-bottom: 1px solid var(--sunk);
}
.t {
  display: flex;
  align-items: center;
  gap: 5px;
  font-weight: 600;
}
.t .ic {
  margin-top: 0;
}
.i .t {
  color: var(--ok);
}
.w .t {
  color: var(--warn);
}
.e .t {
  color: var(--err);
}
.ln {
  font: 12px var(--mono);
  color: var(--muted);
}
.msg {
  overflow-wrap: anywhere;
}
@container (max-width: 560px) {
  li {
    grid-template-columns: 64px minmax(0, 1fr);
  }
  li > :last-child {
    grid-column: 1 / -1;
  }
}
</style>
