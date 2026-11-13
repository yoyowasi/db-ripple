<script setup lang="ts">
// 서버 페이지 이동: "1–100 / 2,340행" + [이전]/[다음].
defineProps<{ text: string; loading?: boolean; canPrev: boolean; canNext: boolean }>()
defineEmits<{ prev: []; next: [] }>()
</script>

<template>
  <div class="pager" role="group" aria-label="행 페이지">
    <span class="rng" role="status">{{ loading ? '읽는 중…' : text }}</span>
    <button type="button" class="btn btn-sm" :disabled="!canPrev" aria-label="이전 페이지" @click="$emit('prev')">이전</button>
    <button type="button" class="btn btn-sm" :disabled="!canNext" aria-label="다음 페이지" @click="$emit('next')">다음</button>
  </div>
</template>

<style scoped>
.pager {
  display: flex;
  align-items: center;
  gap: 8px;
  margin: 6px 0 4px;
  font-size: 12px;
  color: var(--muted);
}
.rng {
  font-variant-numeric: tabular-nums;
}
</style>
