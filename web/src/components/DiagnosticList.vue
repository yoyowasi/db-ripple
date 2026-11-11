<script setup lang="ts">
// 거부(error)·경고(warning) 목록. 규칙 ID, 문장 번호, 위치, 메시지, 설계 문서 참조를 보인다.
import type { Diagnostic } from '../api/types'
import { spanText } from '../lib/format'

defineProps<{ items: Diagnostic[]; emptyText?: string }>()
</script>

<template>
  <p v-if="items.length === 0" class="muted empty">{{ emptyText ?? '없음' }}</p>
  <ul v-else class="diag">
    <li v-for="(d, i) in items" :key="`${d.ruleId}-${i}`">
      <span class="chip" :class="d.severity === 'error' ? 'chip-err' : 'chip-warn'">
        {{ d.severity === 'error' ? '거부' : '경고' }}
      </span>
      <div class="body">
        <div>
          <code>{{ d.ruleId }}</code>
          <span class="muted">
            <template v-if="d.statementIndex !== null"> · 문장 #{{ d.statementIndex }}</template>
            · {{ spanText(d.span) }}
            <template v-if="d.docRef"> · 설계 {{ d.docRef }}</template>
          </span>
        </div>
        <div>{{ d.message }}</div>
      </div>
    </li>
  </ul>
</template>

<style scoped>
.empty {
  margin: 0;
}
.diag {
  list-style: none;
  margin: 0;
  padding: 0;
  display: grid;
  gap: 6px;
}
li {
  display: flex;
  gap: 8px;
  align-items: flex-start;
  font-size: 12.5px;
}
.chip {
  flex: none;
  margin-top: 1px;
}
.body {
  min-width: 0;
  overflow-wrap: anywhere;
}
</style>
