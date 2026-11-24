<script setup lang="ts">
// 오른쪽 위 고정 적용 상자(리디자인 시안 2026-11-23): 큰 합계 숫자("이 쿼리를 적용하면 4행 삭제") → 접이식 적용 조건 n/n · 남은 시간 → [원본에 적용] [미리보기 취소].
// 꺼져 있으면 disabled 대신 aria-disabled 로 두고 이유 목록을 aria-describedby 로 잇는다. 누르면 화면 가운데 확인 대화상자(테이블별 건수 · 원본 DB 이름 입력)가 2단계 확인을 맡는다.
// 적용을 시도한 뒤에는 결과 한 줄과 다음 행동([새 미리보기]·[샌드박스 새로 고침])만 둔다. 자세한 대조는 본문의 ApplyResultView.
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { useNow } from '../composables/useNow'
import { evaluateApplyGate } from '../lib/applyGate'
import { formatCount, formatTime } from '../lib/format'
import { applyStatusLine, netTotals } from '../lib/summary'
import { usePreviewStore } from '../stores/preview'
import { useSandboxStore } from '../stores/sandbox'
import ProblemBanner from './ProblemBanner.vue'
import { gateItems } from './rail/gateItems'
import { mergeNetRows } from './rail/rowOps'

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

/** 확인 상자를 모달로 연다: 네이티브 <dialog>.showModal()이 배경을 inert로 만들고 Tab을 상자 안에 가둔다. */
async function openModal(): Promise<void> {
  confirming.value = true
  await nextTick()
  const d = confirmBox.value as HTMLDialogElement | null
  if (d && !d.open) d.showModal()
  d?.focus()
}
async function ask(): Promise<void> {
  if (!gate.value.enabled) return
  if (confirming.value) return back()
  typedDb.value = ''
  weakOk.value = false
  await openModal()
}
/** 배경(::backdrop)을 눌렀을 때만 닫는다. 입력란에서 끌어 선택하다 밖에서 놓은 경우는 닫지 않는다. */
let downOnBackdrop = false
function onDialogDown(e: MouseEvent): void {
  downOnBackdrop = e.target === e.currentTarget
}
function onDialogClick(e: MouseEvent): void {
  if (downOnBackdrop && e.target === e.currentTarget) void back()
  downOnBackdrop = false
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
    await openModal()
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
  // 원인(거부·실패·기준선 만드는 중)은 결과 자리가 말한다. 여기서는 단추가 꺼진 이유만 짧게.
  return '검증된 미리보기가 없습니다.'
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

/** "남은 30분"(켜져 있을 때만) */
const remain = computed(() => applyStatusLine(gate.value).remain)
const okCount = computed(() => items.value.filter((i) => i.ok).length)

/** 큰 숫자 옆 글자: 한 종류면 "행 삭제", 섞이면 "행 변경" + 아래 줄에 종류별 건수 */
const single = computed(() => {
  const t = totals.value
  if (t.total === 0) return null
  if (t.deleted === t.total) return { verb: '삭제', impact: '삭제됩니다' }
  if (t.inserted === t.total) return { verb: '추가', impact: '추가됩니다' }
  if (t.updated === t.total) return { verb: '수정', impact: '수정됩니다' }
  return null
})
const sumText = computed(() =>
  totals.value.total === 0 ? '행 변경' : `행 ${single.value?.verb ?? '변경'} · ${formatCount(totals.value.tables)}개 테이블`,
)
const bigTone = computed(() => (totals.value.total === 0 ? 'zero' : totals.value.deleted === totals.value.total ? 'del' : 'chg'))
/** 섞였을 때만: "수정 3 · 추가 1" */
const mixLine = computed(() => {
  const t = totals.value
  if (t.total === 0 || single.value) return null
  const parts: string[] = []
  if (t.updated) parts.push(`수정 ${formatCount(t.updated)}`)
  if (t.inserted) parts.push(`추가 ${formatCount(t.inserted)}`)
  if (t.deleted) parts.push(`삭제 ${formatCount(t.deleted)}`)
  return parts.join(' · ')
})
/** 확인 상자 첫 줄(굵은 빨강): "원본에서 4행이 삭제됩니다." */
const impactLine = computed(() => {
  const t = totals.value
  if (t.total === 0) return null
  return `원본에서 ${formatCount(t.total)}행이 ${single.value?.impact ?? '바뀝니다'}.`
})
const warnCount = computed(() => store.preview?.warnings.length ?? 0)

// 합계 숫자 카운트업: 새 미리보기가 올 때만(같은 미리보기의 재계산·움직임 줄이기 설정이면 바로 값).
const shown = ref(0)
let raf = 0
const reducedMotion = () => window.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false
watch(
  () => [store.preview?.previewId, totals.value.total] as const,
  ([id, end], old) => {
    cancelAnimationFrame(raf)
    if (!id || reducedMotion() || (old && old[0] === id)) {
      shown.value = end
      return
    }
    const t0 = performance.now()
    const step = (t: number) => {
      // 첫 프레임의 시각(t)은 t0보다 이를 수 있다. 0 아래를 막지 않으면 -0이나 큰 합계의 음수가 한 프레임 보인다.
      const p = Math.min(1, Math.max(0, (t - t0) / 700))
      shown.value = Math.round(end * (1 - (1 - p) ** 3))
      if (p < 1) raf = requestAnimationFrame(step)
    }
    shown.value = 0
    raf = requestAnimationFrame(step)
  },
  { immediate: true },
)
onBeforeUnmount(() => cancelAnimationFrame(raf))

/** 확인 대화상자의 테이블별 건수 */
const confirmRows = computed(() =>
  mergeNetRows(store.preview?.netSummary ?? [])
    .map((r) => {
      const parts: string[] = []
      if (r.updated) parts.push(`수정 ${formatCount(r.updated)}`)
      if (r.inserted + r.multisetAdded) parts.push(`추가 ${formatCount(r.inserted + r.multisetAdded)}`)
      if (r.deleted + r.multisetRemoved) parts.push(`삭제 ${formatCount(r.deleted + r.multisetRemoved)}`)
      return { table: r.table, text: parts.join(' · ') }
    })
    .filter((x) => x.text),
)

// ---- 적용을 시도한 뒤 ----
const status = computed(() => store.applyResult?.status ?? null)
const RESULT_TITLE: Record<string, string> = {
  committed: '원본에 커밋함',
  drift_detected: '적용 안 함 · 원본이 바뀜',
  rolled_back_mismatch: '적용 안 함 · 결과가 달라 롤백',
}
const resultTone = computed(() => (status.value === 'committed' ? 'ok' : 'bad'))
const resultMeta = computed(() => {
  const r = store.applyResult
  if (!r) return ''
  const ms = Object.values(r.timingsMs).reduce((a, b) => a + b, 0)
  return `${formatTime(r.finishedAt)} · ${(ms / 1000).toFixed(2)}초`
})
/** 적용 뒤 새 기준선이 생겼나: 준비됨이고 기준 시각이 적용 끝난 시각보다 뒤 */
const refreshed = computed(() => {
  const b = sandbox.status?.baselineAt
  const f = store.applyResult?.finishedAt
  return sandbox.status?.state === 'ready' && !!b && !!f && new Date(b).getTime() >= new Date(f).getTime()
})
/** 기준선을 다시 만드는 중. 커밋 직후 상태 조회가 building을 돌려주기 전(아직 옛 기준선이 ready로 보이는 동안)도 포함한다. */
const sandboxBuilding = computed(
  () =>
    sandbox.refreshing ||
    sandbox.status?.state === 'building' ||
    (status.value === 'committed' && !!store.applyResult?.sandboxRefreshStarted && sandbox.status?.state === 'ready' && !refreshed.value),
)
/** 결과를 닫고 편집기로 돌아간다(다음 SQL 쓰기). 편집기가 접혀 있으면 본문으로 초점을 옮긴다. */
function newPreview(): void {
  if (sandboxBuilding.value) return
  store.dismissResult()
  void nextTick(() => {
    const cm = document.querySelector<HTMLElement>('.cm-content')
    if (cm && cm.offsetParent !== null) cm.focus()
    else document.getElementById('main')?.focus()
  })
}
</script>

<template>
  <section class="apply" aria-labelledby="h-apply" :aria-busy="store.applying">
    <h2 id="h-apply" class="sr-only">적용</h2>

    <template v-if="attempted">
      <div class="res" :class="resultTone" role="status">
        <b>
          <svg v-if="resultTone === 'ok'" class="ic done" viewBox="0 0 16 16" aria-hidden="true"><path d="M3 8.5l3.2 3.2L13 4.8" /></svg>
          <svg v-else class="ic" viewBox="0 0 16 16" aria-hidden="true"><path d="M8 2.2L14.2 13H1.8z" /><path d="M8 6.6v3M8 11.4v.1" /></svg>
          {{ RESULT_TITLE[status ?? ''] }}
        </b>
        <span class="t">{{ resultMeta }}</span>
      </div>
      <p v-if="sandboxBuilding" class="building">
        <span class="spin" aria-hidden="true"></span>
        샌드박스 기준선을 다시 만드는 중입니다. 그동안 미리보기는 실행할 수 없습니다.
      </p>
      <p class="hint">
        <template v-if="store.applyResult?.sandboxRefreshStarted && refreshed">샌드박스를 새로 고쳤습니다. </template>
        <template v-else-if="isSplit && status === 'committed' && !store.applyResult?.sandboxRefreshStarted">
          기준선이 오래됨 — 새로 고침이 시작되지 않았습니다. 새로 고친 뒤 다시 미리보기를 실행하세요.
        </template>
        <template v-else-if="!store.applyResult?.sandboxRefreshStarted">새로 고침은 자동으로 시작되지 않았습니다. </template>
        이 미리보기는 끝났습니다. 다시 적용하려면 새 미리보기를 만드세요.
      </p>
      <div class="row">
        <button
          type="button"
          class="btn"
          :class="{ 'btn-primary': status === 'committed' }"
          :aria-disabled="sandboxBuilding"
          :title="sandboxBuilding ? '기준선을 다 만들면 켜집니다' : undefined"
          @click="newPreview"
        >
          새 미리보기
        </button>
        <button
          v-if="!store.applyResult?.sandboxRefreshStarted || status !== 'committed'"
          type="button"
          class="btn"
          :class="{ 'btn-primary': status !== 'committed' }"
          :aria-disabled="refreshing"
          @click="refresh"
        >
          {{ refreshing ? '새로 고치는 중…' : '샌드박스 새로 고침' }}
        </button>
      </div>
    </template>

    <template v-else>
      <div v-if="store.preview" class="sum">
        <div class="lbl">이 쿼리를 적용하면<span v-if="remain" class="remain">{{ remain }}</span></div>
        <div class="big" :class="bigTone">
          <span aria-hidden="true">{{ formatCount(shown) }}</span><span class="sr-only">{{ formatCount(totals.total) }}</span><small>{{ sumText }}</small>
        </div>
        <div v-if="mixLine" class="mix">{{ mixLine }}</div>
      </div>
      <p v-if="emptyNote" class="hint">{{ emptyNote }}</p>

      <!-- 적용 조건: 모두 충족이면 한 줄로 접는다. 미충족이 있으면 펼쳐 이유를 모두 보인다. -->
      <div class="condrow">
        <details v-if="allOk" class="gdet">
          <summary>
            <span class="ck" aria-hidden="true">✓</span>
            <span>
              <span :class="{ wn: warnCount }">{{ warnCount ? `경고 ${formatCount(warnCount)}건` : '경고 없음' }}</span>
              · 적용 조건 {{ okCount }}/{{ items.length }} 충족
            </span>
            <svg class="ic chev" viewBox="0 0 16 16" aria-hidden="true"><path d="M6 4l4 4-4 4" /></svg>
          </summary>
          <ul id="apply-reasons" class="gate">
            <li v-for="it in items" :key="it.label" class="ok">
              <svg class="ic" viewBox="0 0 16 16" aria-hidden="true"><path d="M3 8.5l3.2 3.2L13 4.8" /></svg>
              <span><span class="sr-only">충족: </span>{{ it.label }}</span>
            </li>
          </ul>
        </details>
        <span v-else-if="store.preview" class="cnt bad">조건 {{ okCount }}/{{ items.length }} 충족</span>
      </div>
      <div v-if="!allOk && store.preview" class="gdet bad">
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
      <ul v-else-if="!store.preview" id="apply-reasons" class="sr-only">
        <li v-for="it in items.filter((x) => !x.ok)" :key="it.label">{{ it.label }}</li>
      </ul>

      <!-- 두 단추 모두 누른 뒤에도 사라지지 않게 늘 두고 aria-disabled로 끈다(초점이 body로 떨어지지 않게) -->
      <div class="actrow">
        <button
          ref="applyBtn"
          type="button"
          class="go"
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

      <dialog
        v-if="confirming"
        id="apply-confirm"
        ref="confirmBox"
        class="confirm"
        role="alertdialog"
        tabindex="-1"
        aria-labelledby="cf-h"
        aria-describedby="cf-d"
        @cancel.prevent="back"
        @mousedown="onDialogDown"
        @click="onDialogClick"
      >
      <div class="cfin">
        <h4 id="cf-h">원본 DB 변경 확인</h4>
        <p id="cf-d">
          <b v-if="impactLine" class="impact">{{ impactLine }}</b>
          원본 {{ originDb }}에서 같은 SQL을 다시 실행하고 결과가 같을 때만 커밋합니다. 되돌릴 수 없습니다.
        </p>
        <table v-if="confirmRows.length" class="cfrows">
          <caption class="sr-only">바뀌는 테이블과 건수</caption>
          <tbody>
            <tr v-for="r in confirmRows" :key="r.table">
              <th scope="row" class="mono">{{ r.table }}</th>
              <td>{{ r.text }}</td>
            </tr>
          </tbody>
        </table>
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
            원본에 스냅샷 권한(CREATE DATABASE와 원본 DB CONTROL)이 없어 키 집합·테이블 건수와 미리보기에 없던 테이블의 해시(CHECKSUM)만 비교합니다. 해시 충돌이나 비교할 수 없는 형식의 열 변경은 놓칠 수 있습니다.
          </p>
          <p>원본 데이터가 클수록 [적용] 동안 원본 행 잠금이 길어집니다(합성 1GB에서 약 5초 측정).</p>
          <label class="chk"><input v-model="weakOk" type="checkbox" /> 위 한계를 이해하고 약한 검증으로 적용합니다</label>
        </div>
        <p v-else class="hint">원본에 스냅샷 권한이 없으면 서버가 약한 검증 동의를 물어봅니다.</p>
        <div class="typed">
          <label for="cf-db">확정하려면 원본 DB 이름 <code>{{ org.database }}</code>을 입력하세요</label>
          <input id="cf-db" v-model="typedDb" type="text" class="field" spellcheck="false" autocomplete="off" @keydown.enter.prevent="doApply" />
        </div>
        <div class="row">
          <button type="button" class="btn" @click="back">돌아가기</button>
          <button type="button" class="btn btn-danger cfgo" :aria-disabled="!canConfirm" @click="doApply">{{ confirmLabel }}</button>
        </div>
      </div>
      </dialog>
    </template>

    <ProblemBanner v-if="store.applyError" :error="store.applyError" title="적용 요청이 실패했습니다." />
    <ProblemBanner v-if="store.cancelError" :error="store.cancelError" title="취소하지 못했습니다." />
  </section>
</template>

<style scoped>
.apply {
  display: grid;
  gap: 10px;
  min-width: 0;
  padding: 16px;
}
/* 큰 합계: 이 상자에서 가장 먼저 읽히는 값 */
.sum .lbl {
  display: flex;
  justify-content: space-between;
  gap: 8px;
  color: var(--muted);
  font-size: 13px;
}
.big {
  font-size: 40px;
  font-weight: 700;
  line-height: 1.1;
  letter-spacing: -0.03em;
  font-variant-numeric: tabular-nums;
}
.big small {
  margin-left: 6px;
  color: var(--fg);
  font-size: 15px;
  font-weight: 500;
  letter-spacing: 0;
}
.big.del {
  color: var(--err);
}
.big.zero {
  color: var(--muted);
}
.mix {
  margin-top: 2px;
  color: var(--muted);
  font-size: 13px;
}
.actrow {
  display: flex;
  gap: 8px;
}
.actrow .btn {
  flex: none;
}
.go {
  flex: 1;
  min-width: 0;
  min-height: 38px;
  padding: 6px 10px;
  border: 1px solid var(--accent);
  border-radius: var(--r);
  background: var(--accent);
  color: var(--accent-fg);
  font: 600 14px / 1.25 var(--ui);
  cursor: pointer;
  transition:
    filter var(--m-out) var(--ease),
    transform var(--m-press) var(--ease);
}
.go:not([aria-disabled='true']):hover {
  filter: brightness(1.1);
}
.go:not([aria-disabled='true']):active {
  transform: scale(0.98);
}
.go[aria-disabled='true'] {
  border-color: var(--ctl);
  background: var(--sunk);
  color: var(--muted);
  cursor: not-allowed;
}
.condrow {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 8px;
  font-size: 13px;
  color: var(--muted);
}
.condrow .gdet {
  flex: 1;
  min-width: 0;
}
.ck {
  display: inline-grid;
  place-items: center;
  width: 16px;
  height: 16px;
  border-radius: 50%;
  background: var(--add-bg);
  color: var(--add-fg);
  font-size: 11px;
  font-weight: 800;
}
.cnt.bad {
  color: var(--err);
  font-weight: 600;
}
.remain {
  flex: none;
  font-variant-numeric: tabular-nums;
}
.res {
  display: grid;
  gap: 2px;
}
.res b {
  display: flex;
  gap: 6px;
  align-items: center;
  font-size: 15px;
}
.res.ok b {
  color: var(--ok);
}
.res.bad b {
  color: var(--warn);
}
.res .t {
  color: var(--muted);
  font-size: 12px;
}
.building {
  display: flex;
  gap: 8px;
  align-items: flex-start;
  font-size: 12px;
  color: var(--warn);
}
.spin {
  flex: none;
  width: 12px;
  height: 12px;
  margin-top: 2px;
  border: 2px solid currentColor;
  border-right-color: transparent;
  border-radius: 50%;
  animation: spin 0.9s linear infinite;
}
@keyframes spin {
  to {
    transform: rotate(360deg);
  }
}
@media (prefers-reduced-motion: reduce) {
  .spin {
    animation: none;
  }
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
  list-style: none;
  cursor: pointer;
  font-weight: 600;
  color: var(--ok);
}
.gdet > summary::-webkit-details-marker {
  display: none;
}
.gdet > summary .wn {
  color: var(--warn);
}
.gdet > summary .chev {
  margin-left: auto;
  color: var(--muted);
}
.gdet[open] > summary .chev {
  transform: rotate(90deg);
}
.condrow .gdet:has(summary) {
  padding: 8px 12px;
  border: 1px solid var(--line);
  border-radius: 10px;
}
.lock {
  font-size: 12px;
  color: var(--muted);
}
/* 네이티브 모달 <dialog>: 화면 가운데, 배경은 ::backdrop. 안쪽 .cfin이 여백을 가져 바깥 클릭(배경)과 구분된다. */
.confirm {
  width: min(480px, calc(100vw - 32px));
  max-height: calc(100dvh - 32px);
  margin: auto;
  overflow: auto;
  padding: 0;
  color: var(--fg);
  border: 1px solid var(--ctl);
  border-top: 4px solid var(--err);
  border-radius: var(--r-lg);
  background: var(--panel);
  box-shadow: 0 12px 32px rgb(0 0 0 / 0.25);
}
.confirm::backdrop {
  background: rgb(10 14 22 / 0.45);
}
.cfin {
  padding: 16px;
}
.cfrows {
  width: 100%;
  margin: 0 0 10px;
  border-collapse: collapse;
  font-size: 13px;
}
.cfrows th,
.cfrows td {
  padding: 4px 0;
  border-bottom: 1px solid var(--line);
  text-align: left;
  font-weight: 400;
}
.cfrows td {
  text-align: right;
  font-weight: 600;
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
.impact {
  display: block;
  color: var(--err);
  font-size: 14px;
}
.cfgo {
  flex: 1;
  justify-content: center;
}
@media (prefers-reduced-motion: no-preference) {
  .gdet > summary .chev {
    transition: transform 0.2s var(--ease);
  }
  .confirm[open] {
    animation: dr-pop 0.25s var(--ease);
  }
  .confirm[open]::backdrop {
    animation: dr-fade 0.2s linear;
  }
  /* 적용 완료 체크를 그린다 */
  .done path {
    stroke-dasharray: 16;
    stroke-dashoffset: 16;
    animation: dr-draw 0.45s 0.15s var(--ease) forwards;
  }
}
</style>
