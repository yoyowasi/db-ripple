<script setup lang="ts">
// 문장별 결과 표. 문장이 여러 개이거나 실패·거부·미실행 문장이 있을 때만 쓴다.
import type { StatementResult } from '../../api/types'
import { STATEMENT_STATUS_LABEL, errorHead, execResultParts, formatCount } from '../../lib/format'

/** procedures: EXEC 문장 번호 → "스키마.프로시저". StatementResult에는 이름이 없어(target null) graph의 procedureCall 노드에서 받는다. */
defineProps<{ statements: StatementResult[]; procedures?: ReadonlyMap<number, string> }>()

function chip(s: StatementResult): string {
  return s.status === 'ok' ? 'chip-ok' : s.status === 'not_run' ? '' : 'chip-err'
}
</script>

<template>
  <table class="st">
    <caption class="sr-only">문장별 실행 결과</caption>
    <thead>
      <tr>
        <th scope="col" class="num">#</th>
        <th scope="col">문장</th>
        <th scope="col">결과</th>
      </tr>
    </thead>
    <tbody v-for="s in statements" :key="s.index">
      <tr>
        <td class="num">{{ s.index }}</td>
        <th scope="row">
          <template v-if="s.kind === 'EXEC'">
            EXEC
            <small>{{ procedures?.get(s.index) ?? '(프로시저 이름 없음)' }}</small>
          </template>
          <template v-else>
            {{ s.kind }}
            <small>{{ s.target }}</small>
          </template>
        </th>
        <td class="res">
          <span class="chip" :class="chip(s)">{{ STATEMENT_STATUS_LABEL[s.status] }}</span>
          <small v-if="s.kind === 'EXEC'">
            <!-- 좁은 칸이라 조각마다 줄을 나눈다(낱말 중간에서 끊기지 않게) -->
            <span v-for="p in execResultParts(s)" :key="p" class="ep">{{ p }}</span>
            <span v-if="!execResultParts(s).length" role="img" aria-label="해당 없음">·</span>
          </small>
          <small v-else>
            <template v-if="s.rowsAffected === null"><span role="img" aria-label="해당 없음">·</span></template>
            <template v-else>{{ formatCount(s.rowsAffected) }}행</template>
          </small>
        </td>
      </tr>
      <tr v-if="s.error">
        <td></td>
        <td colspan="2" class="err">
          {{ errorHead(s.error) }}<template v-if="s.error.procedure"> ({{ s.error.procedure }})</template>:
          {{ s.error.message }}
          <span v-if="s.error.isToolFault" class="chip chip-err">도구 오류</span>
        </td>
      </tr>
    </tbody>
  </table>
</template>

<style scoped>
.st {
  width: 100%;
  table-layout: fixed;
  border-collapse: collapse;
  font-size: 12px;
}
.st th,
.st td {
  padding: 4px 4px;
  border-bottom: 1px solid var(--sunk);
  text-align: left;
  vertical-align: top;
  overflow-wrap: anywhere;
}
.st thead th {
  border-bottom-color: var(--line);
  color: var(--muted);
  font-weight: 500;
}
.st th:first-child,
.st td:first-child {
  width: 24px;
  padding-left: 0;
}
.st th:last-child,
.st td:last-child {
  width: 96px;
  padding-right: 0;
}
.st .num {
  text-align: right;
}
.st tbody th {
  font-weight: 500;
}
.st small {
  display: block;
  color: var(--muted);
  font-size: 12px;
}
.st .ep {
  display: block;
}
.st .res small {
  margin-top: 2px;
}
.st .err {
  color: var(--err);
}
</style>
