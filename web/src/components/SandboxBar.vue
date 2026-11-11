<script setup lang="ts">
import { computed } from 'vue'
import type { SandboxState } from '../api/types'
import { formatTime } from '../lib/format'
import { useSandboxStore } from '../stores/sandbox'

const sandbox = useSandboxStore()

const STATE_LABEL: Record<SandboxState, string> = {
  building: '기준선 만드는 중',
  ready: '준비됨',
  dirty: '오염(새로 고침 필요)',
  error: '오류',
}

const st = computed(() => sandbox.status)
const chipClass = computed(() => {
  switch (st.value?.state) {
    case 'ready':
      return 'chip-ok'
    case 'building':
      return 'chip-info'
    case 'dirty':
      return 'chip-warn'
    case 'error':
      return 'chip-err'
    default:
      return ''
  }
})
const busy = computed(() => sandbox.refreshing || st.value?.state === 'building')
</script>

<template>
  <section class="sandbox-bar" aria-label="샌드박스 상태">
    <template v-if="st">
      <span class="chip" :class="chipClass" role="status">샌드박스: {{ STATE_LABEL[st.state] }}</span>
      <span class="muted">기준 시각 {{ formatTime(st.baselineAt) }}</span>
      <span class="chip" :class="st.fingerprintMatch ? 'chip-ok' : 'chip-err'">
        환경 지문 {{ st.fingerprintMatch ? '일치' : '불일치' }}
      </span>
      <span v-if="st.locked" class="chip chip-warn">미리보기·적용 진행 중(잠금)</span>
      <span v-if="st.manifest" class="muted" title="수집 범위 매니페스트">
        수집 {{ st.manifest.coveredTables }}개 · 제외 {{ st.manifest.excludedTables }}개
        <template v-if="st.manifest.auditNotLast > 0"> · 감사 순서 불확실 {{ st.manifest.auditNotLast }}개</template>
      </span>
    </template>
    <span v-else-if="sandbox.statusError" class="chip chip-err" role="alert">{{ sandbox.statusError }}</span>
    <span v-else class="muted">상태 확인 중…</span>
    <button type="button" class="btn btn-sm" :disabled="busy || st?.locked" @click="sandbox.refresh()">
      {{ busy ? '새로 고치는 중…' : '샌드박스 새로 고침' }}
    </button>
  </section>
</template>

<style scoped>
.sandbox-bar {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  font-size: 12.5px;
}
</style>
