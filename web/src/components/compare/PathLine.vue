<script setup lang="ts">
// 아래 창 머리의 경로 한 줄: 문장 → … → 이 테이블. 선 모양이 확신도(실선 확실, 파선 추론), 선 위 글자가 원인.
// 테이블을 여러 묶음이 바꿨으면 묶음마다 한 줄(같은 줄은 합침). 순서는 관측 순서일 뿐 실행 순서가 아니다.
import { computed } from 'vue'
import type { PreviewResult } from '../../api/types'
import { tablePaths } from '../../lib/compare'

const props = defineProps<{ preview: PreviewResult; table: string }>()
const paths = computed(() => tablePaths(props.preview, props.table))
</script>

<template>
  <div v-if="paths.length" class="paths">
    <ol v-for="(p, i) in paths" :key="i" class="path" :aria-label="`경로 ${i + 1}`">
      <li v-for="(it, j) in p" :key="j" :class="it.type">
        <span v-if="it.type === 'link'" class="lk" :class="{ inf: !it.certain }" :title="it.title">
          <em>{{ it.text }}</em>
          <i aria-hidden="true"></i>
          <span class="sr-only">({{ it.conf }})</span>
        </span>
        <span v-else class="nd" :class="{ stmt: it.type === 'stmt', self: it.type === 'table' && it.here }" :title="it.title">{{ it.text }}</span>
      </li>
    </ol>
  </div>
</template>

<style scoped>
.paths {
  display: grid;
  gap: 4px;
}
.path {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-end;
  margin: 0;
  padding: 0;
  list-style: none;
  font-size: 12px;
}
.path li {
  display: flex;
  align-items: flex-end;
}
.nd {
  padding: 1px 7px;
  border: 1px solid var(--ctl);
  border-radius: var(--r);
  background: var(--panel);
  white-space: nowrap;
}
.nd.stmt {
  background: var(--fg);
  border-color: var(--fg);
  color: var(--panel);
  font-weight: 600;
}
.nd.self {
  border: 2px solid var(--accent);
  padding: 0 6px;
  font-weight: 700;
}
.lk {
  display: inline-flex;
  flex-direction: column;
  align-items: center;
  min-width: 44px;
  padding: 0 4px 9px;
}
.lk em {
  font-style: normal;
  font-size: 11px;
  line-height: 1.3;
  color: var(--muted);
  white-space: nowrap;
}
.lk i {
  display: block;
  align-self: stretch;
  border-top: 2px solid var(--fg);
}
.lk.inf i {
  border-top: 2px dashed var(--muted);
}
</style>
