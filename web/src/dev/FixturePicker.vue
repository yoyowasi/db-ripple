<script setup lang="ts">
// 메뉴 줄의 "시험 데이터" 선택(개발 서버에서만). 고르면 ?fixture= 를 바꿔 화면을 처음부터 다시 연다.
import { currentFixture } from './fakeApi'
import { FIXTURE_IDS, FIXTURES } from './fixtures'

const current = currentFixture() ?? ''

function onChange(e: Event): void {
  const v = (e.target as HTMLSelectElement).value
  const u = new URL(window.location.href)
  if (v) u.searchParams.set('fixture', v)
  else u.searchParams.delete('fixture')
  window.location.assign(u.toString())
}
</script>

<template>
  <div class="fixture">
    <span class="tag">개발 전용</span>
    <label for="dev-fixture">시험 데이터</label>
    <select id="dev-fixture" :value="current" aria-describedby="dev-fixture-desc" @change="onChange">
      <option value="">끔(실제 API)</option>
      <option v-for="id in FIXTURE_IDS" :key="id" :value="id">{{ id }} · {{ FIXTURES[id].label }}</option>
    </select>
    <span id="dev-fixture-desc" class="sr-only">제품 기능이 아닙니다. 백엔드 없이 화면 상태를 보는 개발용 목록이며 고르면 화면을 다시 엽니다.</span>
  </div>
</template>

<style scoped>
.fixture {
  display: flex;
  align-items: center;
  gap: 8px;
}
.tag {
  color: #9db2bf;
  border: 1px dashed var(--menu-ctl);
  border-radius: var(--r);
  padding: 0 6px;
}
select {
  font: 12px var(--mono);
  height: 22px;
  min-height: 0;
  max-width: 300px;
  padding: 0 6px;
  color: #e6f0f5;
  background: #1f3340;
  border: 1px solid var(--menu-ctl);
}
@media (max-width: 999px) {
  .tag {
    display: none;
  }
}
</style>
