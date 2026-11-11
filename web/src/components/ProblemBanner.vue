<script setup lang="ts">
import { computed } from 'vue'
import type { ApiError } from '../api/client'
import { conflictNeedsRefresh, describeApiError } from '../api/client'
import { spanText } from '../lib/format'
import { useSandboxStore } from '../stores/sandbox'

const props = defineProps<{ error: ApiError; title: string }>()

const sandbox = useSandboxStore()
const text = computed(() => describeApiError(props.error))
const needsRefresh = computed(() => props.error.status === 409 && conflictNeedsRefresh(props.error.reason))
</script>

<template>
  <div class="banner banner-err" role="alert">
    <div>
      <strong>{{ title }}</strong>
      <span>{{ text }}</span>
      <span v-if="error.problem?.title && error.status !== 0" class="muted"> ({{ error.problem.title }})</span>
      <ul v-if="error.rejects.length" class="rejects">
        <li v-for="(r, i) in error.rejects" :key="i">
          <code>{{ r.ruleId }}</code>
          <template v-if="r.statementIndex !== null"> · 문장 #{{ r.statementIndex }}</template>
          · {{ spanText(r.span) }}
        </li>
      </ul>
      <p v-if="needsRefresh" class="act">
        <button type="button" class="btn btn-sm" :disabled="sandbox.refreshing" @click="sandbox.refresh()">
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
