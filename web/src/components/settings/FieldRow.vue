<script setup lang="ts">
// 설정 폼의 한 줄: label for · 도움말 · 오류(aria-describedby·aria-invalid는 슬롯으로 넘겨 입력 칸이 붙인다).
import { computed } from 'vue'

const props = defineProps<{ id: string; label: string; error?: string; hint?: string }>()

const hintId = computed(() => (props.hint ? `${props.id}-hint` : undefined))
const errId = computed(() => (props.error ? `${props.id}-err` : undefined))
const describedby = computed(() => [hintId.value, errId.value].filter(Boolean).join(' ') || undefined)
</script>

<template>
  <div class="row">
    <label :for="id">{{ label }}</label>
    <div class="ctl">
      <slot :describedby="describedby" :invalid="!!error" />
      <p v-if="hint" :id="hintId" class="hint">{{ hint }}</p>
      <p v-if="error" :id="errId" class="err" role="alert">{{ error }}</p>
    </div>
  </div>
</template>

<style scoped>
.row {
  display: grid;
  grid-template-columns: 150px minmax(0, 1fr);
  gap: 4px 12px;
  align-items: start;
}
label {
  padding-top: 4px;
  font-weight: 500;
}
.ctl {
  display: grid;
  gap: 3px;
  min-width: 0;
}
.ctl :slotted(input[type='text']),
.ctl :slotted(input[type='password']) {
  width: 100%;
  max-width: 420px;
}
.hint {
  font-size: 12px;
  color: var(--muted);
}
.err {
  font-size: 12px;
  font-weight: 600;
  color: var(--err);
}
@media (max-width: 640px) {
  .row {
    grid-template-columns: minmax(0, 1fr);
  }
  label {
    padding-top: 0;
  }
}
</style>
