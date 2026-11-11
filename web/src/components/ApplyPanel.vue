<script setup lang="ts">
// C4(적용 부분): [적용]·[취소]. [적용]은 status ok · 환경 지문 일치 · 30분 안 · SQL 불변일 때만 켜지고,
// 꺼져 있으면 이유를 모두 보인다. 원본에 COMMIT 하므로 한 번 더 확인한다.
import { computed, nextTick, ref, watch } from 'vue'
import { useNow } from '../composables/useNow'
import { evaluateApplyGate } from '../lib/applyGate'
import { mmss } from '../lib/format'
import { usePreviewStore } from '../stores/preview'
import { useSandboxStore } from '../stores/sandbox'
import ApplyResultView from './ApplyResultView.vue'
import ProblemBanner from './ProblemBanner.vue'

const store = usePreviewStore()
const sandbox = useSandboxStore()
const now = useNow(5000)

const gate = computed(() =>
  evaluateApplyGate({
    preview: store.preview,
    sandbox: sandbox.status,
    editorSql: store.sql,
    previewSql: store.previewSql,
    now: now.value,
    applyResult: store.applyResult,
    consumed: store.consumed,
    busy: store.applying || store.running,
  }),
)

const confirming = ref(false)
// 확인 단계의 기본 포커스는 안전한 쪽("돌아가기")이다. Enter를 연달아 눌러도 COMMIT되지 않는다.
const backBtn = ref<HTMLButtonElement | null>(null)
const applyBtn = ref<HTMLButtonElement | null>(null)

// 확인 중에 조건이 깨지면 확인 단계를 닫는다.
watch(
  () => gate.value.enabled,
  (on) => {
    if (!on) confirming.value = false
  },
)

async function ask(): Promise<void> {
  confirming.value = true
  await nextTick()
  backBtn.value?.focus()
}
async function back(): Promise<void> {
  confirming.value = false
  await nextTick()
  applyBtn.value?.focus()
}
async function doApply(): Promise<void> {
  confirming.value = false
  await store.applyNow()
}

const originDb = computed(() => store.preview?.baseline.originDb ?? '원본')
const remainingText = computed(() => {
  const r = gate.value.remainingMs
  if (r === null) return ''
  return r > 0 ? `만료까지 ${mmss(r)}` : '만료됨'
})
const canCancel = computed(() => store.running || (!!store.preview && !store.consumed && !store.applying))
const cancelLabel = computed(() => (store.running ? '중단' : '취소(미리보기 폐기)'))
</script>

<template>
  <section class="card" aria-label="적용">
    <header>
      <h2>적용</h2>
      <span v-if="remainingText" class="chip" :class="gate.remainingMs !== null && gate.remainingMs <= 0 ? 'chip-err' : ''">
        {{ remainingText }}
      </span>
    </header>

    <div class="card-body stack">
      <div class="actions">
        <button
          ref="applyBtn"
          type="button"
          class="btn btn-primary"
          :disabled="!gate.enabled"
          :aria-describedby="gate.enabled ? undefined : 'apply-reasons'"
          @click="ask"
        >
          {{ store.applying ? '적용 중…' : '적용' }}
        </button>
        <button type="button" class="btn" :disabled="!canCancel" @click="store.cancel()">{{ cancelLabel }}</button>
      </div>

      <div v-if="confirming" class="banner banner-warn" role="alertdialog" aria-labelledby="confirm-title">
        <div>
          <strong id="confirm-title">원본 {{ originDb }}에 적용할까요?</strong>
          같은 SQL을 원본에서 다시 실행해 미리보기와 같은 결과인지 확인한 뒤에만 COMMIT합니다. 다르면 자동으로 롤백합니다. COMMIT한 뒤에는 되돌릴 수 없습니다.
          <p class="confirm-actions">
            <button ref="backBtn" type="button" class="btn" @click="back">돌아가기</button>
            <button type="button" class="btn btn-danger" @click="doApply">예, 적용합니다</button>
          </p>
        </div>
      </div>

      <div v-if="!gate.enabled && !store.applyResult" id="apply-reasons" class="reasons" role="note">
        <strong>[적용]을 쓸 수 없는 이유</strong>
        <ul>
          <li v-for="(r, i) in gate.reasons" :key="i">{{ r }}</li>
        </ul>
      </div>
      <p v-else-if="gate.enabled" class="muted ok-note">
        조건을 모두 만족합니다: 상태 정상 · 환경 지문 일치 · 30분 안 · SQL 변경 없음.
      </p>

      <ProblemBanner v-if="store.applyError" :error="store.applyError" title="적용 요청이 실패했습니다." />
      <ProblemBanner v-if="store.cancelError" :error="store.cancelError" title="취소하지 못했습니다." />

      <ApplyResultView v-if="store.applyResult" :result="store.applyResult" />
    </div>
  </section>
</template>

<style scoped>
.stack {
  display: grid;
  gap: 10px;
}
.actions {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
}
.reasons {
  font-size: 12.5px;
  color: var(--fg-muted);
}
.reasons strong {
  color: var(--fg);
}
.reasons ul {
  margin: 4px 0 0;
  padding-left: 18px;
}
.ok-note {
  margin: 0;
  font-size: 12.5px;
}
.confirm-actions {
  display: flex;
  gap: 8px;
  margin: 8px 0 0;
}
</style>
