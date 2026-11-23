<script setup lang="ts">
// 샌드박스 상태(리디자인 시안 2026-11-23 상단바): 왼쪽 "원본 → 샌드박스" · 기준 시각, 오른쪽 초록 점 + 한 줄 글씨(배경 상자 없음) · [샌드박스 새로 고침] · [테마].
// 정상이면 조용하게, 문제가 있으면 짧은 표시가 옆에 붙는다. 상태 글씨를 누르면 모드 · 원본 · 기준 시각 · 환경 지문(차단·경고) · 인스턴스 경고 · 원본 연결 · 잠금이 열린다.
// App.vue 메뉴 줄의 #menu-slot 으로 Teleport 한다(홈 화면에서만 보인다).
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import type { SandboxState } from '../api/types'
import { useTheme } from '../lib/theme'
import { usePreviewStore } from '../stores/preview'
import { useSandboxStore } from '../stores/sandbox'

const sandbox = useSandboxStore()
const preview = usePreviewStore()
const { theme, toggle } = useTheme()

const STATE_LABEL: Record<SandboxState, string> = {
  building: '샌드박스 기준선을 만드는 중',
  ready: '샌드박스 준비됨',
  dirty: '샌드박스 오염 · 새로 고침 필요',
  error: '샌드박스 오류',
}
const DOT: Record<SandboxState, string> = { building: 'busy', ready: 'ok', dirty: 'warn', error: 'err' }

const st = computed(() => sandbox.status)

/** "2026-11-12 14:27"(브라우저 지역 시각) */
function shortTime(iso: string): string {
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return iso
  const p = (v: number) => String(v).padStart(2, '0')
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`
}
const busy = computed(() => sandbox.refreshing || st.value?.state === 'building')
const refreshBlocked = computed(() => busy.value || !!st.value?.locked)
/** 새로 고침이 필요한 상태면 단추를 주 단추로 */
const refreshUrgent = computed(() => st.value?.baselineStale === true || st.value?.state === 'dirty' || st.value?.state === 'error')

/** 버전 문자열은 미리보기 결과의 baseline.fingerprint 에만 있다(GET /sandbox 에는 일치 여부만). */
const fingerprintText = computed(() => {
  const f = preview.preview?.baseline.fingerprint
  if (!f) return null
  if (st.value?.engine === 'postgres') return `PostgreSQL ${f.productVersion} · ${f.collation}`
  return `SQL Server ${f.productVersion} · ${f.edition} · 호환성 수준 ${f.compatibilityLevel} · ${f.collation}`
})
/** 오류 상태의 원인(서버가 error일 때만 보낸다). */
const lastError = computed(() => (st.value?.state === 'error' ? (st.value.lastError ?? null) : null))

const modeLabel = computed(() => (st.value?.mode === 'split' ? '분리' : st.value?.mode === 'single_server' ? '한 서버' : null))
const modeTitle = computed(() =>
  st.value?.mode === 'split' ? '원본은 개발 서버, 샌드박스는 이 PC입니다.' : '원본과 샌드박스가 한 서버에 있습니다.',
)
/** 원본 표시: 분리면 "서버/DB", 한 서버면 DB만(서버는 샌드박스와 같다). */
const originText = computed(() => {
  const o = st.value?.origin
  if (o) return st.value?.mode === 'split' && o.server ? `${o.server}/${o.database}` : o.database
  return preview.preview?.baseline.originDb ?? null
})
/** 샌드박스 DB 이름은 미리보기 결과에만 있다 */
const sandboxDb = computed(() => preview.preview?.baseline.sandboxDb ?? null)
const stale = computed(() => st.value?.baselineStale === true)
const unreachable = computed(() => st.value?.originReachable === false)
const mismatches = computed(() => st.value?.fingerprintMismatches ?? [])
const warnings = computed(() => st.value?.fingerprintWarnings ?? [])
/** 샌드박스 인스턴스 설정 경고(xp_cmdshell 켜짐·연결된 서버 있음 등). 막지 않는다. 옛 응답에는 필드가 없다. */
const instanceWarnings = computed(() => st.value?.instanceWarnings ?? [])

/** 정상이 아닐 때만 칩 옆에 붙는 짧은 표시 */
const flags = computed(() => {
  const s = st.value
  if (!s) return []
  const out: { text: string; tone: 'warn' | 'err' }[] = []
  if (!s.fingerprintMatch || mismatches.value.length) out.push({ text: '환경 지문 불일치', tone: 'err' })
  if (stale.value) out.push({ text: '기준선 오래됨', tone: 'warn' })
  if (unreachable.value) out.push({ text: '원본 연결 안 됨', tone: 'warn' })
  if (s.locked) out.push({ text: '잠금: 다른 작업 진행 중', tone: 'warn' })
  if (instanceWarnings.value.length) out.push({ text: `인스턴스 경고 ${instanceWarnings.value.length}`, tone: 'warn' })
  return out
})

function refresh(): void {
  if (!refreshBlocked.value) void sandbox.refresh()
}

// 팝오버: 바깥을 누르거나 Esc면 닫는다.
const det = ref<HTMLDetailsElement | null>(null)
function onDoc(e: MouseEvent): void {
  if (det.value?.open && !det.value.contains(e.target as Node)) det.value.open = false
}
function onEsc(e: KeyboardEvent): void {
  if (e.key === 'Escape' && det.value?.open) {
    det.value.open = false
    det.value.querySelector('summary')?.focus()
  }
}
onMounted(() => document.addEventListener('mousedown', onDoc))
onBeforeUnmount(() => document.removeEventListener('mousedown', onDoc))
</script>

<template>
  <Teleport defer to="#menu-slot">
    <div class="sbar" role="region" aria-label="샌드박스 상태와 테마" :aria-busy="busy">
      <span v-if="originText" class="dbpair mono" :title="modeLabel ? modeTitle : undefined">
        {{ originText }}<template v-if="sandboxDb"> → {{ sandboxDb }}</template>
      </span>
      <span v-if="st?.baselineAt" class="base">기준 <time :datetime="st.baselineAt">{{ shortTime(st.baselineAt) }}</time></span>
      <span class="sp"></span>
      <details v-if="st" ref="det" class="st" @keydown="onEsc">
        <summary :aria-label="`${STATE_LABEL[st.state]}${flags.length ? `, ${flags.map((f) => f.text).join(', ')}` : ''}. 자세히 보기`">
          <span class="dot" :class="DOT[st.state]" aria-hidden="true"></span>
          <span role="status">{{ STATE_LABEL[st.state] }}</span>
          <small v-if="st.state === 'ready' && !flags.length" class="calm">· 잠금 없음</small>
          <svg class="chev" viewBox="0 0 16 16" aria-hidden="true"><path d="M4 6l4 4 4-4" /></svg>
        </summary>
        <div class="pop">
          <template v-if="lastError">
            <h3>실패 이유</h3>
            <p class="bad why">{{ lastError }}</p>
          </template>
          <dl>
            <template v-if="modeLabel">
              <dt>연결</dt>
              <dd>{{ modeLabel }} <span class="muted">· {{ modeTitle }}</span></dd>
            </template>
            <template v-if="originText">
              <dt>원본</dt>
              <dd class="mono">{{ originText }}</dd>
            </template>
            <template v-if="sandboxDb">
              <dt>샌드박스</dt>
              <dd class="mono">{{ sandboxDb }}</dd>
            </template>
            <template v-if="st.baselineAt">
              <dt>기준 시각</dt>
              <dd>
                <time :datetime="st.baselineAt">{{ shortTime(st.baselineAt) }}</time>
                <span v-if="stale" class="bad"> · 원본이 그 뒤 바뀜</span>
              </dd>
            </template>
            <dt>환경 지문</dt>
            <dd>
              <span :class="{ bad: !st.fingerprintMatch }">{{ st.fingerprintMatch ? '원본과 일치' : '원본과 불일치' }}</span>
              <span v-if="fingerprintText" class="muted"><br />{{ fingerprintText }}</span>
            </dd>
            <template v-if="st.manifest">
              <dt>수집 범위</dt>
              <dd>테이블 {{ st.manifest.coveredTables }}개 · 제외 {{ st.manifest.excludedTables }}개</dd>
            </template>
            <dt>원본 연결</dt>
            <dd :class="{ bad: unreachable }">{{ unreachable ? '연결 안 됨 · 지문 비교 보류' : '연결됨' }}</dd>
            <dt>잠금</dt>
            <dd>{{ st.locked ? '다른 미리보기·적용 진행 중' : '없음' }}</dd>
          </dl>
          <template v-if="mismatches.length">
            <h3>차단(미리보기·적용이 막힙니다)</h3>
            <ul><li v-for="m in mismatches" :key="m">{{ m }}</li></ul>
          </template>
          <template v-if="warnings.length">
            <h3>지문 경고(막지는 않습니다)</h3>
            <ul><li v-for="w in warnings" :key="w">{{ w }}</li></ul>
          </template>
          <template v-if="instanceWarnings.length">
            <h3>샌드박스 인스턴스 경고(막지는 않습니다)</h3>
            <ul><li v-for="w in instanceWarnings" :key="w">{{ w }}</li></ul>
          </template>
        </div>
      </details>
      <b v-else-if="sandbox.statusError" class="err" role="alert">{{ sandbox.statusError }}</b>
      <span v-else class="muted" role="status">샌드박스 상태 확인 중…</span>
      <span v-for="f in flags" :key="f.text" class="flag" :class="f.tone">{{ f.text }}</span>
      <button
        v-if="st"
        type="button"
        class="btn btn-sm"
        :class="{ 'btn-primary': refreshUrgent }"
        :aria-disabled="refreshBlocked"
        :title="st.locked ? '다른 미리보기·적용이 끝나면 켜집니다' : undefined"
        @click="refresh"
      >
        {{ sandbox.refreshing ? '새로 고치는 중…' : '샌드박스 새로 고침' }}
      </button>
      <button type="button" class="btn btn-sm thm" :aria-pressed="theme === 'dark'" title="어두운 테마" @click="toggle">
        <svg class="ic" viewBox="0 0 16 16" aria-hidden="true">
          <circle cx="8" cy="8" r="5.8" />
          <path d="M8 2.2a5.8 5.8 0 0 1 0 11.6z" fill="currentColor" />
        </svg>
        테마<span class="sr-only">: 어두운 테마</span>
      </button>
    </div>
  </Teleport>
</template>

<style scoped>
.sbar {
  display: flex;
  align-items: center;
  gap: 10px;
  min-width: 0;
}
/* 메뉴 줄이 좁으면 왼쪽 정보(DB 이름·기준 시각)부터 말줄임으로 줄인다. 상태와 단추는 줄이지 않는다. */
.sbar > * {
  flex: none;
}
.sbar > .dbpair,
.sbar > .base {
  flex: 0 1 auto;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
}
.dbpair {
  color: var(--menu-fg);
  font-size: 12px;
  white-space: nowrap;
}
.base {
  color: var(--muted);
  font-size: 13px;
  white-space: nowrap;
}
.sbar > .sp {
  flex: 1;
}
.calm {
  color: var(--muted);
  font-size: 13px;
}
.st {
  position: relative;
}
.st > summary {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 3px 6px;
  border-radius: var(--r);
  color: var(--menu-fg-strong);
  font-size: 13px;
  list-style: none;
  cursor: pointer;
  white-space: nowrap;
}
.st > summary::-webkit-details-marker {
  display: none;
}
.st > summary:hover {
  background: var(--sunk);
  color: var(--menu-fg-strong);
}
.chev {
  width: 12px;
  height: 12px;
  fill: none;
  stroke: currentColor;
  stroke-width: 1.6;
}
.dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: var(--ok);
}
.dot.busy {
  background: var(--warn);
  animation: pulse 1s ease-in-out infinite;
}
.dot.warn {
  background: var(--warn);
}
.dot.err {
  background: var(--err);
}
@keyframes pulse {
  50% {
    opacity: 0.35;
  }
}
@media (prefers-reduced-motion: reduce) {
  .dot.busy {
    animation: none;
  }
}
.pop {
  position: absolute;
  top: calc(100% + 6px);
  left: 0;
  z-index: 40;
  display: grid;
  gap: 8px;
  justify-items: start;
  width: min(420px, calc(100vw - 32px));
  padding: 12px 14px;
  border: 1px solid var(--ctl);
  border-radius: 6px;
  background: var(--panel);
  color: var(--fg);
  box-shadow: 0 8px 24px rgb(0 0 0 / 0.18);
  font-size: 13px;
}
.pop dl {
  display: grid;
  grid-template-columns: max-content minmax(0, 1fr);
  gap: 4px 14px;
  margin: 0;
  width: 100%;
}
.pop dt {
  color: var(--muted);
}
.pop dd {
  margin: 0;
  overflow-wrap: anywhere;
}
.pop h3 {
  margin: 4px 0 0;
  font: 600 12px var(--ui);
}
.pop ul {
  margin: 0;
  padding-left: 18px;
  font-size: 12px;
}
.bad {
  color: var(--err);
  font-weight: 600;
}
/* 실패 이유는 서버 메시지라 길 수 있다: 줄바꿈을 살리고 긴 낱말도 접는다. */
.why {
  margin: 0 0 10px;
  font-weight: 500;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
  max-height: 160px;
  overflow: auto;
}
.err {
  color: var(--err);
  font-size: 12px;
}
.flag {
  padding: 1px 8px;
  border-radius: 99px;
  font-size: 12px;
  font-weight: 600;
  white-space: nowrap;
}
.flag.warn {
  background: var(--warn-bg);
  color: var(--warn);
}
.flag.err {
  background: var(--err-bg);
  color: var(--err);
}
.sbar .btn {
  font-size: 13px;
}
.thm {
  gap: 6px;
}
</style>
