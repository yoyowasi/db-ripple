<script setup lang="ts">
// SQL 띠 오른쪽 "미리보기 정상 · 1.2초 ▾"를 누르면 열리는 상세: 문장별 결과 · 수집 범위 · 미리보기 id · 환경 지문 · 단계별 소요 · 내보내기.
// 판정에 필요한 숫자(테이블별 건수)는 비교표가 맡고, 여기는 확인용 자세한 값만 둔다.
import { computed } from 'vue'
import type { PreviewResult } from '../../api/types'
import PreviewDetails from '../rail/PreviewDetails.vue'
import StatementTable from '../rail/StatementTable.vue'

const props = defineProps<{ preview: PreviewResult; consumed: boolean }>()

// EXEC 문장의 프로시저 이름: StatementResult.procedure가 우선이고, 없으면(예전 응답) graph의 procedureCall 노드에서 읽는다.
const procedures = computed(() => {
  const m = new Map(
    props.preview.graph.nodes.flatMap((n) => (n.type === 'procedureCall' ? [[n.statementIndex, n.procedure] as [number, string]] : [])),
  )
  for (const s of props.preview.statements) if (s.procedure) m.set(s.index, s.procedure)
  return m
})

// 롤백 뒤 남은 변경·수집 범위 한 줄
const footLine = computed(() => {
  const p = props.preview
  const parts: string[] = []
  if (p.residualCheckFailed) parts.push('롤백 뒤 남은 변경을 확인하지 못했습니다')
  else if (p.residualCommitted.length === 0) parts.push('롤백 뒤 남은 변경 없음')
  else parts.push(`롤백 뒤 남은 변경 ${p.residualCommitted.length}건`)
  const keysOnly = p.capture.truncated || p.firings.some((f) => f.imageMode === 'KEYS')
  parts.push(keysOnly ? '수집 범위: 키만(행 이미지 상한 초과)' : '수집 범위 전체')
  return parts.join(' · ')
})
</script>

<template>
  <div class="pi">
    <p class="foot">{{ footLine }}</p>
    <section aria-labelledby="pi-st">
      <h3 id="pi-st">문장별 결과</h3>
      <StatementTable :statements="preview.statements" :procedures="procedures" />
    </section>
    <PreviewDetails :preview="preview" :consumed="consumed" />
  </div>
</template>

<style scoped>
.pi {
  display: grid;
  gap: 10px;
}
.foot {
  color: var(--muted);
  font-size: 12px;
}
.pi h3 {
  margin-bottom: 4px;
  font: 600 12px var(--ui);
}
</style>
