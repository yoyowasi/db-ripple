<script setup lang="ts">
// ProblemDetails 오류 알림(role=alert). 409는 사유 문장과 필요하면 [샌드박스 새로 고침], 422는 거부 규칙·위치를 붙인다.
import { computed } from 'vue'
import type { ApiError } from '../api/client'
import { conflictNeedsRefresh, describeApiError } from '../api/client'
import { spanText } from '../lib/format'
import { useSandboxStore } from '../stores/sandbox'

const props = defineProps<{ error: ApiError; title: string }>()

const sandbox = useSandboxStore()
const text = computed(() => describeApiError(props.error))
const needsRefresh = computed(() => props.error.status === 409 && conflictNeedsRefresh(props.error.reason))
const showProblemTitle = computed(() => {
  const t = props.error.problem?.title
  return !!t && props.error.status !== 0 && !text.value.includes(t)
})
</script>

<template>
  <div class="banner banner-err" role="alert">
    <svg class="ic" viewBox="0 0 16 16" aria-hidden="true">
      <circle cx="8" cy="8" r="5.8" />
      <path d="M4 12L12 4" />
    </svg>
    <div>
      <b>{{ title }}</b>
      <span>{{ text }}</span>
      <span v-if="showProblemTitle" class="muted"> ({{ error.problem?.title }})</span>
      <ul v-if="error.rejects.length" class="rejects">
        <li v-for="(r, i) in error.rejects" :key="i">
          <code>{{ r.ruleId }}</code>
          <template v-if="r.statementIndex !== null"> · 문장 {{ r.statementIndex }}</template>
          · {{ spanText(r.span) }}
        </li>
      </ul>
      <p v-if="needsRefresh" class="act">
        <button type="button" class="btn btn-sm" :aria-disabled="sandbox.refreshing" @click="!sandbox.refreshing && sandbox.refresh()">
          샌드박스 새로 고침
        </button>
      </p>
    </div>
  </div>
</template>

<style scoped>
.rejects {
  margin: 4px 0 0;
  padding-left: 18px;
}
.act {
  margin: 6px 0 0;
}
</style>
