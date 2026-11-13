<script setup lang="ts">
// 레일 아래 고정 적용 영역: 적용 조건(✓/✗) · [원본에 적용](주) · [미리보기 취소](보조) · 같은 자리 확인 단계.
// 꺼져 있으면 disabled 대신 aria-disabled 로 두고 이유 목록을 aria-describedby 로 잇는다. 적용 결과는 PreviewSummary 맨 위 패널이 그린다.
import { computed, nextTick, ref, watch } from 'vue'
import { useNow } from '../composables/useNow'
import { evaluateApplyGate } from '../lib/applyGate'
import { formatCount } from '../lib/format'
import { netTotals } from '../lib/summary'
import { usePreviewStore } from '../stores/preview'
import { useSandboxStore } from '../stores/sandbox'
import ProblemBanner from './ProblemBanner.vue'
import { gateItems } from './rail/gateItems'

const store = usePreviewStore()
const sandbox = useSandboxStore()
const now = useNow(1000) // HomeView와 같은 간격: 만료 판정 시계가 어긋나지 않게

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
const items = computed(() => gateItems(gate.value, !!store.preview))
const allOk = computed(() => gate.value.enabled)

// 적용을 시도한 뒤(확정·드리프트·불일치)에는 조건 목록 대신 새로 고침 안내를 둔다.
const attempted = computed(() => {
  const s = store.applyResult?.status
  return s === 'committed' || s === 'drift_detected' || s === 'rolled_back_mismatch'
})

const confirming = ref(false)
/** 확인 단계 입력: 원본 DB 이름을 직접 쳐야 확정 단추가 켜진다(N4 계약 §6). */
const typedDb = ref('')
/** 약한 검증(KEYSET_HASH) 한계를 이해하고 동의했다 → allowKeysetVerification:true */
const weakOk = ref(false)

const isSplit = computed(() => sandbox.status?.mode === 'split')
// 원본 표시는 SandboxStatus.origin(없으면 미리보기의 originDb만).
const org = computed(() => {
  const o = sandbox.status?.origin
  return {
    server: o?.server ?? null,
    database: o?.database ?? store.preview?.baseline.originDb ?? '',
    login: o?.login ?? null,
    sysadmin: o?.isSysadmin === true,
  }
})
const dbMatches = computed(() => org.value.database !== '' && typedDb.value.trim().toLowerCase() === org.value.database.toLowerCase())
const needsWeak = computed(() => store.needsWeakConsent)
const canConfirm = computed(() => dbMatches.value && (!needsWeak.value || weakOk.value))
const applyBtn = ref<HTMLButtonElement | null>(null)
const confirmBox = ref<HTMLElement | null>(null)

// 확인 중에 조건이 깨지면(만료·SQL 변경 등) 확인 단계를 닫는다.
watch(
  () => gate.value.enabled,
  (on) => {
    if (!on) closeConfirm()
  },
)

/** 확인 단계를 닫는다. 포커스가 확인 상자 안에 있었으면 body로 떨어지지 않게 [원본에 적용] 단추(없으면 결과 패널 제목)로 옮긴다. */
function closeConfirm(): void {
  const hadFocus = !!confirmBox.value?.contains(document.activeElement)
  confirming.value = false
  if (!hadFocus) return
  void nextTick(focusApplyOrResult)
}
function focusApplyOrResult(): void {
  ;(applyBtn.value ?? document.getElementById('h-apply-res'))?.focus()
}

async function ask(): Promise<void> {
  if (!gate.value.enabled) return
  if (confirming.value) return back()
  typedDb.value = ''
  weakOk.value = false
  confirming.value = true
  await nextTick()
  confirmBox.value?.focus()
}
async function back(): Promise<void> {
  confirming.value = false
  await nextTick()
  applyBtn.value?.focus()
}
async function doApply(): Promise<void> {
  if (!canConfirm.value) return
  const allowKeyset = needsWeak.value && weakOk.value
  const keep = typedDb.value
  closeConfirm()
  await store.applyNow(allowKeyset)
  // 서버가 약한 검증 동의를 요구했다: 같은 확인 상자를 한계 설명·동의 체크와 함께 다시 연다(DB 이름 입력은 유지).
  if (store.needsWeakConsent) {
    typedDb.value = keep
    weakOk.value = false
    confirming.value = true
    await nextTick()
    confirmBox.value?.focus()
    return
  }
  // 커밋·드리프트 등으로 [원본에 적용] 단추가 사라져 포커스를 잃었으면 결과 패널 제목으로 옮긴다.
  await nextTick()
  const a = document.activeElement
  if (!a || a === document.body) focusApplyOrResult()
}
function onCancel(): void {
  if (canCancel.value) void store.cancel()
}

// 미리보기가 없을 때 레일이 비어 보이지 않게 이유를 한 줄 덧붙인다(rail-top 문구와 겹치지 않는 원인 쪽).
const emptyNote = computed(() => {
  if (store.preview || store.running) return null
  if (store.runError?.status === 422) return '거부된 문장이 있어 미리보기가 없습니다. 편집기의 진단을 고친 뒤 다시 실행하세요.'
  if (store.runError) return '미리보기를 만들지 못했습니다. 원인은 결과 영역의 알림에 있습니다.'
  if (sandbox.status?.state === 'building') return '샌드박스 기준선을 만드는 중입니다. 끝나면 미리보기를 실행할 수 있습니다.'
  return null
})

const originDb = computed(() => store.preview?.baseline.originDb ?? '원본')
const totals = computed(() => netTotals(store.preview?.netSummary ?? []))
// 확정 단추 라벨은 결과를 말한다: 한 종류면 "원본에서 4행 삭제", 섞이면 "원본에 4행 변경 적용".
const confirmLabel = computed(() => {
  const t = totals.value
  if (t.total === 0) return '원본에 적용'
  const n = formatCount(t.total)
  if (t.deleted === t.total) return `원본에서 ${n}행 삭제`
  if (t.inserted === t.total) return `원본에 ${n}행 추가`
  if (t.updated === t.total) return `원본에서 ${n}행 수정`
  return `원본에 ${n}행 변경 적용`
})

const canCancel = computed(() => store.running || (!!store.preview && !store.consumed && !store.applying))
const cancelLabel = computed(() => (store.running ? '중단' : '미리보기 취소'))

const refreshing = computed(() => sandbox.refreshing)
function refresh(): void {
  if (!refreshing.value) void sandbox.refresh()
}
</script>

<template>
  <section class="apply" aria-labelledby="h-apply" :aria-busy="store.applying">
    <h2 id="h-apply" class="sr-only">적용</h2>

    <template v-if="attempted">
      <div class="row">
        <button
          type="button"
          class="btn"
          :class="{ 'btn-primary': store.applyResult?.status !== 'committed' }"
          :aria-disabled="refreshing"
          @click="refresh"
        >
          {{ refreshing ? '새로 고치는 중…' : '샌드박스 새로 고침' }}
        </button>
      </div>
      <p class="hint">
        <template v-if="store.applyResult?.sandboxRefreshStarted">샌드박스 새로 고침을 시작했습니다. </template>
        <template v-else-if="isSplit && store.applyResult?.status === 'committed'">
          기준선이 오래됨 — 새로 고침이 시작되지 않았습니다. 새로 고친 뒤 다시 미리보기를 실행하세요.
        </template>
        <template v-else>새로 고침은 자동으로 시작되지 않았습니다. </template>
        이 미리보기는 다시 적용할 수 없습니다. 새 미리보기를 만드세요.
      </p>
    </template>

    <template v-else>
      <p v-if="emptyNote" class="hint">{{ emptyNote }}</p>
      <!-- 적용 조건: 모두 충족이면 한 줄로 접는다. 미충족이 있으면 펼쳐 이유를 모두 보인다. -->
      <details v-if="allOk" class="gdet">
        <summary>
          <svg class="ic" viewBox="0 0 16 16" aria-hidden="true"><path d="M3 8.5l3.2 3.2L13 4.8" /></svg>
          적용 조건 {{ items.length }}개 모두 충족
        </summary>
        <ul id="apply-reasons" class="gate">
          <li v-for="it in items" :key="it.label" class="ok">
            <svg class="ic" viewBox="0 0 16 16" aria-hidden="true"><path d="M3 8.5l3.2 3.2L13 4.8" /></svg>
            <span><span class="sr-only">충족: </span>{{ it.label }}</span>
          </li>
        </ul>
      </details>
      <div v-else class="gdet bad">
        <h3 class="gh">적용 조건</h3>
        <ul id="apply-reasons" class="gate">
          <li v-for="it in items" :key="it.label" :class="it.ok ? 'ok' : 'no'">
            <svg v-if="it.ok" class="ic" viewBox="0 0 16 16" aria-hidden="true"><path d="M3 8.5l3.2 3.2L13 4.8" /></svg>
            <svg v-else class="ic" viewBox="0 0 16 16" aria-hidden="true"><path d="M4 4l8 8M12 4l-8 8" /></svg>
            <span>
              <span class="sr-only">{{ it.ok ? '충족: ' : '미충족: ' }}</span>{{ it.label }}
              <small v-for="w in it.why" :key="w">{{ w }}</small>
            </span>
          </li>
        </ul>
      </div>

      <div class="row">
        <button
          ref="applyBtn"
          type="button"
          class="btn"
          :class="{ 'btn-primary': !!store.preview && !confirming }"
          :aria-disabled="!gate.enabled"
          :aria-describedby="gate.enabled ? undefined : 'apply-reasons'"
          :aria-expanded="gate.enabled ? confirming : undefined"
          :aria-controls="gate.enabled ? 'apply-confirm' : undefined"
          @click="ask"
        >
          {{ store.applying ? '적용 중…' : '원본에 적용' }}
        </button>
        <button type="button" class="btn" :aria-disabled="!canCancel" @click="onCancel">{{ cancelLabel }}</button>
      </div>

      <p v-if="store.applying" class="lock" role="status">
        원본 {{ originDb }}에서 같은 SQL을 실행하는 중입니다. 끝날 때까지 다른 미리보기와 적용은 잠깁니다.
      </p>

      <div
        v-if="confirming"
        id="apply-confirm"
        ref="confirmBox"
        class="confirm"
        role="alertdialog"
        tabindex="-1"
        aria-labelledby="cf-h"
        aria-describedby="cf-d"
        @keydown.esc.stop="back"
      >
        <h4 id="cf-h">원본 DB 변경 확인</h4>
        <p id="cf-d">원본 {{ originDb }}에서 같은 SQL을 다시 실행하고 결과가 같을 때만 커밋합니다. 되돌릴 수 없습니다.</p>
        <dl class="org">
          <template v-if="org.server">
            <dt>서버</dt>
            <dd class="mono">{{ org.server }}</dd>
          </template>
          <dt>DB</dt>
          <dd class="mono">{{ org.database }}</dd>
          <template v-if="org.login">
            <dt>로그인</dt>
            <dd class="mono">{{ org.login }}</dd>
          </template>
        </dl>
        <p v-if="org.sysadmin" class="warnline">원본 로그인이 sysadmin입니다. 권한이 넓으니 대상 DB가 맞는지 한 번 더 확인하세요.</p>
        <div v-if="needsWeak" class="weak">
          <b>약한 검증(KEYSET_HASH)으로만 적용할 수 있습니다</b>
          <p>
            원본에 스냅샷(CREATE DATABASE) 권한이 없어 키 집합·테이블 건수와 미리보기에 없던 테이블의 해시(CHECKSUM)만 비교합니다. 해시 충돌이나 비교할 수 없는 형식의 열 변경은 놓칠 수 있습니다.
          </p>
          <label class="chk"><input v-model="weakOk" type="checkbox" /> 위 한계를 이해하고 약한 검증으로 적용합니다</label>
        </div>
        <p v-else class="hint">원본에 스냅샷 권한이 없으면 서버가 약한 검증 동의를 물어봅니다.</p>
        <div class="typed">
          <label for="cf-db">확정하려면 원본 DB 이름 <code>{{ org.database }}</code>을 입력하세요</label>
          <input id="cf-db" v-model="typedDb" type="text" class="field" spellcheck="false" autocomplete="off" @keydown.enter.prevent="doApply" />
        </div>
        <div class="row">
          <button type="button" class="btn btn-primary" :aria-disabled="!canConfirm" @click="doApply">{{ confirmLabel }}</button>
          <button type="button" class="btn" @click="back">돌아가기</button>
        </div>
      </div>
    </template>

    <ProblemBanner v-if="store.applyError" :error="store.applyError" title="적용 요청이 실패했습니다." />
    <ProblemBanner v-if="store.cancelError" :error="store.cancelError" title="취소하지 못했습니다." />
  </section>
</template>

<style scoped>
.apply {
  display: grid;
  gap: 8px;
  min-width: 0;
  padding: 8px var(--gx) 12px;
}
.row {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}
.gate {
  display: grid;
  gap: 3px;
  margin: 6px 0 0;
  padding: 0;
  list-style: none;
  font-size: 13px;
}
.gate li {
  display: flex;
  gap: 8px;
  align-items: flex-start;
  min-width: 0;
}
.gate li > span {
  min-width: 0;
  overflow-wrap: anywhere;
}
.gate .ic {
  margin-top: 3px;
}
.gate .ok .ic {
  color: var(--ok);
}
.gate .no .ic {
  color: var(--err);
}
.gate .no {
  color: var(--err);
  font-weight: 500;
}
.gate small {
  display: block;
  font-weight: 400;
  font-size: 12px;
}
.gh {
  margin: 0;
}
.gdet > summary {
  display: flex;
  gap: 6px;
  align-items: center;
  cursor: pointer;
  font-weight: 600;
  color: var(--ok);
}
.gdet > summary .ic {
  color: var(--ok);
}
.lock {
  font-size: 12px;
  color: var(--muted);
}
.confirm {
  padding: 10px;
  border: 1px solid var(--err);
  border-radius: var(--r);
  background: var(--err-bg);
}
.confirm:focus {
  outline: none;
}
.confirm:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}
.org {
  display: grid;
  grid-template-columns: max-content minmax(0, 1fr);
  gap: 1px 10px;
  margin: 0 0 8px;
  font-size: 12px;
}
.org dt {
  color: var(--muted);
}
.org dd {
  margin: 0;
  overflow-wrap: anywhere;
}
.warnline {
  padding: 4px 8px;
  border: 1px solid var(--warn);
  border-radius: var(--r);
  background: var(--warn-bg);
  font-size: 12px;
}
.weak {
  margin: 0 0 8px;
  padding: 6px 8px;
  border: 1px solid var(--warn);
  border-radius: var(--r);
  background: var(--warn-bg);
  font-size: 12px;
}
.weak p {
  margin: 2px 0 6px;
}
.chk {
  display: inline-flex;
  gap: 6px;
  align-items: flex-start;
  cursor: pointer;
}
.typed {
  display: grid;
  gap: 3px;
  margin: 0 0 8px;
  font-size: 12px;
}
.typed .field {
  width: 100%;
}
.confirm h4 {
  margin: 0 0 4px;
  font: 600 14px var(--ui);
}
.confirm p {
  margin: 0 0 8px;
}
</style>
