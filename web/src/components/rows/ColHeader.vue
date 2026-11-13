<script setup lang="ts">
// 열 머리: 열 이름 + "예상값"·"파생" 뱃지(툴팁에 이유). AG Grid가 params 하나로 넘긴다.
import type { IHeaderParams } from 'ag-grid-community'
import { computed } from 'vue'
import type { VolatileReason } from '../../api/types'
import { VOLATILE_LABEL } from '../../lib/format'

export interface ColHeaderParams extends IHeaderParams {
  /** 열 이름 툴팁(타입·NULL 허용) */
  tip?: string
  volatile?: VolatileReason
  derived?: boolean
}

const props = defineProps<{ params: ColHeaderParams }>()

const volTip = computed(() =>
  props.params.volatile
    ? `${VOLATILE_LABEL[props.params.volatile]}: 적용 때 값이 달라질 수 있어 존재와 형식만 비교합니다.`
    : '',
)
</script>

<template>
  <span class="ch">
    <span class="nm" :title="params.tip ?? params.displayName">{{ params.displayName }}</span>
    <span v-if="params.volatile" class="chip chip-vol" :title="volTip">예상값</span>
    <span v-if="params.derived" class="chip chip-der" title="계산 열이라 직접 바뀐 값이 아닙니다.">파생</span>
  </span>
</template>

<style scoped>
.ch {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
  width: 100%;
}
.nm {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.chip {
  flex: none;
  font-family: var(--ui);
  font-weight: 600;
}
.chip-vol {
  background: transparent;
  color: var(--fg);
  border-color: var(--muted);
}
.chip-der {
  font-style: italic;
}
</style>
