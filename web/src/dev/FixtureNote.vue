<script setup lang="ts">
// 시험 데이터 모드일 때 가운데 영역 맨 아래에 근거와 예시값을 밝힌다(개발 서버에서만).
import { currentFixture } from './fakeApi'
import { FIXTURES } from './fixtures'

const id = currentFixture()
const fx = id ? FIXTURES[id] : null
</script>

<template>
  <p v-if="fx" class="note"><b>시험 데이터 {{ fx.id }}</b> 근거: {{ fx.source }} · <b>예시값</b>: {{ fx.examples }}</p>
</template>

<style scoped>
.note {
  flex: none;
  margin: 0;
  padding: 4px var(--gx);
  border-top: 1px dashed var(--ctl);
  background: var(--sunk);
  color: var(--muted);
  font-size: 12px;
}
.note b {
  color: var(--fg);
  font-weight: 600;
}
</style>
