<script setup lang="ts">
// NetChange 목록(롤백 뒤 남은 변경). 테이블, 키, 변경 종류, 바뀐 열을 한 줄씩 보인다.
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
  <p v-if="items.length === 0" class="muted empty">{{ emptyText ?? '없음' }}</p>
  <table v-else class="data">
    <thead>
      <tr>
        <th>테이블</th>
        <th>키</th>
        <th>변경</th>
        <th>바뀐 열</th>
      </tr>
    </thead>
    <tbody>
      <tr v-for="(c, i) in items" :key="i">
        <td>{{ c.table }}</td>
        <td class="mono">{{ keyText(c.key) }}</td>
        <td>{{ summary(c) }}</td>
        <td>{{ c.changedColumns.join(', ') || '-' }}</td>
      </tr>
    </tbody>
  </table>
</template>

<style scoped>
.empty {
  margin: 0;
}
</style>
