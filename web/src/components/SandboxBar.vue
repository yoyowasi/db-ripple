<script setup lang="ts">
// 샌드박스 띠: 탐색기 접기 · 상태 · 모드 · 원본 · 기준 시각(오래됨) · 환경 지문(일치/불일치, 차단·경고 목록) · 원본 연결 · 잠금 · [샌드박스 새로 고침] · 테마.
import { computed } from 'vue'
import type { SandboxState } from '../api/types'
import { useTheme } from '../lib/theme'
import { usePreviewStore } from '../stores/preview'
import { useSandboxStore } from '../stores/sandbox'

defineProps<{ explorerOpen: boolean }>()
const emit = defineEmits<{ 'toggle-explorer': [] }>()

const sandbox = useSandboxStore()
const preview = usePreviewStore()
const { theme, toggle } = useTheme()

const STATE_LABEL: Record<SandboxState, string> = {
  building: '샌드박스 기준선을 만드는 중',
  ready: '샌드박스 준비됨',
  dirty: '샌드박스 오염 · 새로 고침 필요',
  error: '샌드박스 오류',
}
const DOT: Record<SandboxState, string> = { building: 'busy', ready: 'ok', dirty: 'busy', error: 'err' }

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

/** 버전 문자열은 미리보기 결과의 baseline.fingerprint 에만 있다(GET /sandbox 에는 일치 여부만). */
const fingerprintTitle = computed(() => {
  const f = preview.preview?.baseline.fingerprint
  if (!f) return undefined
  return `SQL Server ${f.productVersion} · ${f.edition} · 호환성 수준 ${f.compatibilityLevel} · ${f.collation}`
})

const modeLabel = computed(() => (st.value?.mode === 'split' ? '분리' : st.value?.mode === 'single_server' ? '한 서버' : null))
const modeTitle = computed(() =>
  st.value?.mode === 'split' ? '원본은 개발 서버, 샌드박스는 이 PC입니다.' : '원본과 샌드박스가 한 서버에 있습니다.',
)
/** 원본 표시: 분리면 "서버/DB", 한 서버면 DB만(서버는 샌드박스와 같다). */
const originText = computed(() => {
  const o = st.value?.origin
  if (!o) return null
  return st.value?.mode === 'split' && o.server ? `${o.server}/${o.database}` : o.database
})
const stale = computed(() => st.value?.baselineStale === true)
const unreachable = computed(() => st.value?.originReachable === false)
const mismatches = computed(() => st.value?.fingerprintMismatches ?? [])
const warnings = computed(() => st.value?.fingerprintWarnings ?? [])
const fpSummary = computed(() => {
  const parts: string[] = []
  if (mismatches.value.length) parts.push(`차단 ${mismatches.value.length}`)
  if (warnings.value.length) parts.push(`경고 ${warnings.value.length}`)
  return parts.join(' · ')
})

function refresh(): void {
  if (!refreshBlocked.value) void sandbox.refresh()
}
</script>

<template>
  <div class="bar" role="region" aria-label="샌드박스 상태와 테마" :aria-busy="busy">
    <button type="button" class="btn btn-sm" :aria-expanded="explorerOpen" aria-controls="explorer" @click="emit('toggle-explorer')">
      {{ explorerOpen ? '탐색기 접기' : '탐색기 펼치기' }}
    </button>
    <div class="sbx">
      <template v-if="st">
        <span class="dot" :class="DOT[st.state]" aria-hidden="true"></span>
        <b role="status">{{ STATE_LABEL[st.state] }}</b>
        <span v-if="modeLabel" class="chip chip-info" :title="modeTitle">{{ modeLabel }}</span>
        <span v-if="originText">원본 <span class="mono org">{{ originText }}</span></span>
        <span v-if="st.baselineAt">기준 시각 <time :datetime="st.baselineAt">{{ shortTime(st.baselineAt) }}</time></span>
        <span v-if="stale" class="chip chip-warn" title="원본이 기준선을 만든 뒤 바뀌었습니다. 새로 고치기 전의 미리보기는 오래된 데이터를 기준으로 합니다.">
          기준선 오래됨 · 새로 고침
        </span>
        <span
          class="fp"
          :class="{ bad: !st.fingerprintMatch, tip: !!fingerprintTitle }"
          :title="fingerprintTitle"
          :tabindex="fingerprintTitle ? 0 : undefined"
        >
          환경 지문 {{ st.fingerprintMatch ? '일치' : '불일치' }}
          <span v-if="fingerprintTitle" class="sr-only"> ({{ fingerprintTitle }})</span>
        </span>
        <details v-if="fpSummary" class="fpd">
          <summary :class="{ bad: mismatches.length > 0 }" :aria-label="`환경 지문 ${fpSummary}, 자세히 보기`">지문 {{ fpSummary }}</summary>
          <div class="fpd-body">
            <template v-if="mismatches.length">
              <h3>차단(미리보기·적용이 막힙니다)</h3>
              <ul><li v-for="m in mismatches" :key="m">{{ m }}</li></ul>
            </template>
            <template v-if="warnings.length">
              <h3>경고(막지는 않습니다)</h3>
              <ul><li v-for="w in warnings" :key="w">{{ w }}</li></ul>
            </template>
          </div>
        </details>
        <span v-if="unreachable" class="chip chip-warn" role="status">원본 연결 안 됨 · 지문 비교 보류</span>
        <span>{{ st.locked ? '잠금: 다른 미리보기·적용 진행 중' : '잠금 없음' }}</span>
      </template>
      <b v-else-if="sandbox.statusError" class="err" role="alert">{{ sandbox.statusError }}</b>
      <span v-else class="muted" role="status">샌드박스 상태 확인 중…</span>
      <button type="button" class="btn btn-sm" :class="{ 'btn-primary': stale }" :aria-disabled="refreshBlocked" @click="refresh">
        {{ sandbox.refreshing ? '새로 고치는 중…' : '샌드박스 새로 고침' }}
      </button>
    </div>
    <button type="button" class="btn btn-sm" :aria-pressed="theme === 'dark'" @click="toggle">
      <svg class="ic" viewBox="0 0 16 16" aria-hidden="true">
        <circle cx="8" cy="8" r="5.8" />
        <path d="M8 2.2a5.8 5.8 0 0 1 0 11.6z" fill="currentColor" />
      </svg>
      <span>어두운 테마</span>
    </button>
  </div>
</template>

<style scoped>
.bar {
  display: flex;
  align-items: center;
  gap: 12px;
  flex-wrap: wrap;
  min-height: 36px;
  min-width: 0;
  padding: 3px var(--gx);
  background: var(--panel);
  border-bottom: 1px solid var(--line);
}
.bar > .btn {
  flex: none;
}
.sbx {
  display: flex;
  flex: 1;
  flex-wrap: wrap;
  align-items: center;
  gap: 4px 16px;
  min-width: 0;
}
.sbx b {
  font-weight: 600;
}
.dot {
  width: 9px;
  height: 9px;
  margin-right: -10px;
  border-radius: 50%;
  background: var(--ok);
}
.dot.busy {
  background: var(--warn);
}
.dot.err {
  background: var(--err);
}
.org {
  font-size: 12px;
}
.fpd {
  position: relative;
}
.fpd > summary {
  cursor: pointer;
  list-style: none;
  border-bottom: 1px dotted var(--muted);
}
.fpd > summary::-webkit-details-marker {
  display: none;
}
.fpd > summary.bad {
  color: var(--err);
  font-weight: 600;
}
.fpd-body {
  position: absolute;
  top: calc(100% + 6px);
  left: 0;
  z-index: 20;
  width: min(420px, calc(100vw - 32px));
  padding: 8px 12px;
  background: var(--panel);
  border: 1px solid var(--line);
  border-radius: var(--r);
  box-shadow: 0 6px 18px rgb(0 0 0 / 0.18);
}
.fpd-body h3 {
  margin: 4px 0 2px;
}
.fpd-body ul {
  margin: 0 0 4px;
  padding-left: 18px;
}
.fp.tip {
  border-bottom: 1px dotted var(--muted);
  cursor: help;
}
.fp.bad,
.err {
  color: var(--err);
  font-weight: 600;
}
</style>
