<script setup lang="ts">
// NetChange 목록(롤백 뒤 남은 변경). 레일 폭에 맞춰 한 건을 두 줄로 보인다: 테이블·키 / 변경 종류·바뀐 열.
import type { NetChange } from '../api/types'
import { NET_CHANGE_LABEL, keyText } from '../lib/format'

defineProps<{ items: NetChange[]; emptyText?: string }>()

function summary(c: NetChange): string {
  if (c.change === 'MULTISET_REMOVED') return `제거된 행 ${c.multiplicity}개`
  if (c.change === 'MULTISET_ADDED') return `추가된 행 ${c.multiplicity}개`
  return NET_CHANGE_LABEL[c.change]
}
</script>

<template>
  <p v-if="items.length === 0" class="empty">{{ emptyText ?? '없음' }}</p>
  <ul v-else class="nc">
    <li v-for="(c, i) in items" :key="i">
      <span class="t">{{ c.table }}</span>
      <code class="k">{{ keyText(c.key) }}</code>
      <span class="m">
        {{ summary(c) }}
        <template v-if="c.changedColumns.length"> · {{ c.changedColumns.join(', ') }}</template>
      </span>
    </li>
  </ul>
</template>

<style scoped>
.empty {
  margin: 0;
  color: var(--muted);
  font-size: 12px;
}
.nc {
  display: grid;
  gap: 6px;
  margin: 0;
  padding: 0;
  list-style: none;
  font-size: 12px;
}
.nc li {
  min-width: 0;
  overflow-wrap: anywhere;
}
.t {
  font-weight: 600;
}
.k {
  margin-left: 6px;
}
.m {
  display: block;
  color: var(--muted);
}
</style>
