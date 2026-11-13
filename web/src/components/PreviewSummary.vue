<script setup lang="ts">
// 오른쪽 레일 본문: (적용 결과) → 테이블별 변경 행 수(순변경) → 경고와 참고 → 문장별 결과 → 잔여 변경 → 자세히.
// 헤드라인·적용 가능 줄은 HomeView 의 rail-top 이 그린다.
import { computed } from 'vue'
import type { PreviewResult } from '../api/types'
import { OP_LABEL } from '../lib/format'
import { causeBreakdown, netTotals } from '../lib/summary'
import { usePreviewStore } from '../stores/preview'
import ApplyResultView from './ApplyResultView.vue'
import DiagnosticList from './DiagnosticList.vue'
import NetChangeList from './NetChangeList.vue'
import NetSummaryTable from './rail/NetSummaryTable.vue'
import PreviewDetails from './rail/PreviewDetails.vue'
import StatementTable from './rail/StatementTable.vue'

const props = defineProps<{ preview: PreviewResult; consumed: boolean }>()

const store = usePreviewStore()

const breakdown = computed(() => causeBreakdown(props.preview.firings))
const hasRows = computed(() => netTotals(props.preview.netSummary).total > 0)

const STATUS_NOTE: Partial<Record<PreviewResult['status'], string>> = {
  failed: '문장이 실패해 트랜잭션을 롤백했습니다. 원본은 바뀌지 않았습니다. 오류는 아래 문장별 결과에 있습니다.',
  trigger_blocked: '트리거가 ROLLBACK 하거나 거부했습니다(E13). 원본에서도 같은 결과가 납니다.',
}

// 0행 묶음: 트리거·INSTEAD OF 뿐이면 "트리거 쓰기", 아니면 일반 "쓰기"로 부른다.
const zeroLabel = computed(() => {
  const z = breakdown.value.zeroRow
  const trig = z.every((f) => f.cause.kind === 'TRIGGER' || f.cause.kind === 'INSTEAD_OF')
  return `변경 없는 ${trig ? '트리거 ' : ''}쓰기 ${z.length}건`
})
function zeroText(id: string, table: string, op: keyof typeof OP_LABEL, cands: string[]): string {
  const n = store.firingOrder.get(id)
  return `${n ? `#${n} ` : ''}${table} ${OP_LABEL[op]} 0행${cands.length ? ` · ${cands.join(', ')}` : ''}`
}

// 롤백 뒤 남은 변경·수집 범위 한 줄
const footLine = computed(() => {
  const p = props.preview
  const parts: string[] = []
  if (p.residualCheckFailed) parts.push('롤백 뒤 남은 변경을 확인하지 못했습니다')
  else if (p.residualCommitted.length === 0) parts.push('롤백 뒤 남은 변경 없음')
  const keysOnly = p.capture.truncated || p.firings.some((f) => f.imageMode === 'KEYS')
  parts.push(keysOnly ? '수집 범위: 키만(행 이미지 상한 초과)' : '수집 범위 전체')
  return parts.join(' · ')
})

const inferredTrigger = computed(() =>
  props.preview.firings.some((f) => f.cause.kind === 'TRIGGER' && f.cause.confidence === 'inferred'),
)
const showStatements = computed(
  () => props.preview.statements.length > 1 || props.preview.statements.some((s) => s.status !== 'ok'),
)
</script>

<template>
  <div class="summary">
    <ApplyResultView v-if="store.applyResult" :result="store.applyResult" :preview="preview" />

    <div v-if="preview.residualCheckFailed" class="sec">
      <div class="banner banner-err" role="alert">
        <svg class="ic" viewBox="0 0 16 16" aria-hidden="true"><circle cx="8" cy="8" r="5.8" /><path d="M4 12L12 4" /></svg>
        <div>
          <strong>잔여 확인 실패</strong>
          롤백 뒤 남은 변경을 확인하지 못했습니다. 남은 변경이 없다고 말할 수 없고 샌드박스는 오염 상태가 됩니다.
          <span v-if="preview.residualCheckMessage" class="why">사유: {{ preview.residualCheckMessage }}</span>
        </div>
      </div>
    </div>

    <section class="sec" aria-labelledby="h-sum">
      <h3 id="h-sum">테이블별 변경 행 수(순변경)</h3>
      <template v-if="hasRows">
        <NetSummaryTable :preview="preview" :basis="breakdown.basisByTable" />
        <p class="hint">합계와 위 문구는 순변경 기준입니다. 발동 기준은 보조 정보라 합과 다를 수 있습니다.</p>
      </template>
      <template v-else>
        <p class="none">
          <svg class="ic" viewBox="0 0 16 16" aria-hidden="true"><circle cx="8" cy="8" r="5.8" /><path d="M8 7.4v3.6M8 5v.1" /></svg>
          <span>변경 행 없음</span>
        </p>
        <p v-if="STATUS_NOTE[preview.status]" class="hint">{{ STATUS_NOTE[preview.status] }}</p>
      </template>

      <details v-if="breakdown.zeroRow.length" class="zero">
        <summary>{{ zeroLabel }}</summary>
        <ul>
          <li v-for="f in breakdown.zeroRow" :key="f.id">
            {{ zeroText(f.id, f.table, f.operation, f.cause.triggerCandidates) }}
          </li>
        </ul>
      </details>

      <p class="hint">{{ footLine }}</p>
    </section>

    <section class="sec" aria-labelledby="h-warn">
      <h3 id="h-warn">경고와 참고</h3>
      <p v-if="preview.warnings.length === 0" class="none ok">
        <svg class="ic" viewBox="0 0 16 16" aria-hidden="true"><path d="M3 8.5l3.2 3.2L13 4.8" /></svg>
        <span>경고 없음</span>
      </p>
      <DiagnosticList v-else :items="preview.warnings" />
      <p v-if="inferredTrigger" class="hint">트리거로 인한 변경의 귀속은 추론입니다(그래프의 파선).</p>
    </section>

    <section v-if="showStatements" class="sec" aria-labelledby="h-st">
      <h3 id="h-st">문장별 결과</h3>
      <StatementTable :statements="preview.statements" />
    </section>

    <section v-if="preview.residualCommitted.length" class="sec" aria-labelledby="h-residual">
      <h3 id="h-residual">롤백 뒤 남은 변경 {{ preview.residualCommitted.length }}건</h3>
      <NetChangeList :items="preview.residualCommitted" />
      <p class="hint">트리거가 롤백 뒤에 남긴 변경입니다. 샌드박스는 오염으로 표시되고 다시 만들어집니다.</p>
    </section>

    <div class="sec">
      <PreviewDetails :preview="preview" :consumed="consumed" />
    </div>
  </div>
</template>

<style scoped>
.summary {
  min-width: 0;
}
.sec {
  padding: 8px var(--gx) 12px;
  border-bottom: 1px solid var(--line);
  min-width: 0;
}
.sec:last-child {
  border-bottom: 0;
}
.sec > h3 {
  margin-bottom: 6px;
}
.sec .hint {
  margin-top: 6px;
}
.none {
  display: flex;
  gap: 6px;
  align-items: center;
  color: var(--muted);
}
.none.ok .ic {
  color: var(--ok);
}
.why {
  display: block;
  margin-top: 2px;
  color: var(--muted);
  overflow-wrap: anywhere;
}
.zero {
  margin-top: 8px;
  color: var(--muted);
  font-size: 12px;
}
.zero > summary {
  cursor: pointer;
}
.zero > summary:hover {
  color: var(--fg);
}
.zero ul {
  margin: 6px 0 0;
  padding-left: 16px;
  overflow-wrap: anywhere;
}
</style>
