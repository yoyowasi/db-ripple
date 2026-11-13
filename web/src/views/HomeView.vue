<script setup lang="ts">
// 워크벤치 틀(목업 v6): 샌드박스 띠 · 왼쪽 탐색기 · 가운데 위 편집기 · 가운데 아래 결과(전파 그래프 | 행 변경 + 서랍 자리) · 오른쪽 레일.
// 그래프·레일·행 변경 컴포넌트 내부는 2단계에서 바뀐다. 여기서는 자리와 상태 문구만 정한다.
import { computed, defineAsyncComponent, nextTick, onBeforeUnmount, onMounted, shallowRef, watch } from 'vue'
import type { ApplyStatus, PreviewStatus } from '../api/types'
import ApplyPanel from '../components/ApplyPanel.vue'
import DiagnosticList from '../components/DiagnosticList.vue'
import DiffGrid from '../components/DiffGrid.vue'
import ExplorerTree from '../components/ExplorerTree.vue'
import GraphDrawer from '../components/GraphDrawer.vue'
import PreviewSummary from '../components/PreviewSummary.vue'
import ProblemBanner from '../components/ProblemBanner.vue'
import PropagationGraph from '../components/PropagationGraph.vue'
import SandboxBar from '../components/SandboxBar.vue'
import SqlEditor from '../components/SqlEditor.vue'
import { useNow } from '../composables/useNow'
import { useSplitter } from '../composables/useSplitter'
import { evaluateApplyGate } from '../lib/applyGate'
import { mmss } from '../lib/format'
import { applyStatusLine, previewHeadline, tableBadges } from '../lib/summary'
import type { ResultTab } from '../stores/preview'
import { usePreviewStore } from '../stores/preview'
import { useSandboxStore } from '../stores/sandbox'

// 개발 서버에서만: 시험 데이터 근거 표시. 운영 빌드에서는 빠진다.
const FixtureNote = import.meta.env.DEV ? defineAsyncComponent(() => import('../dev/FixtureNote.vue')) : null

const store = usePreviewStore()
const sandbox = useSandboxStore()
const now = useNow(1000)

// ---- 배치: 좁은 폭(1000px 미만)에서는 탐색기를 접고 레일을 아래로 ----

const NARROW = '(max-width: 999px)'
const mq = window.matchMedia(NARROW)
const explorerOpen = shallowRef(!mq.matches)
function onMedia(e: MediaQueryListEvent): void {
  if (e.matches) explorerOpen.value = false
}

const xSplit = useSplitter({ key: 'explorer', axis: 'x', min: 168, max: 400, initial: 256 })
const ySplit = useSplitter({ key: 'editor', axis: 'y', min: 68, max: 420, initial: 176 })
const editorCollapsed = shallowRef(false)

onMounted(() => {
  mq.addEventListener('change', onMedia)
  if (import.meta.env.DEV) void import('../dev/boot').then((m) => m.bootFixture())
  // 돌아왔을 때 현재 SQL을 한 번 분석해 둔다.
  if (store.sql.trim() && !store.analysis) void store.analyzeNow()
})
onBeforeUnmount(() => mq.removeEventListener('change', onMedia))

// ---- 편집기 ----

const elapsed = computed(() => (store.runStartedAt ? mmss(now.value - store.runStartedAt) : ''))
const rejected = computed(() => !!store.analysis && !store.analysis.isAccepted && !store.analyzing)

/** 실행할 수 없는 이유(실행 중이면 null: 단추가 진행 문구를 보인다). */
const runBlockedReason = computed<string | null>(() => {
  if (store.running) return null
  if (store.applying) return '원본에 적용하는 중이라 실행할 수 없습니다.'
  if (!store.sql.trim()) return 'SQL을 입력하세요.'
  if (rejected.value) return '거부된 문장이 있어 실행할 수 없습니다. 진단 줄을 확인하세요.'
  if (sandbox.status?.state === 'building') return '샌드박스 기준선을 만드는 중입니다.'
  return null
})
const runDisabled = computed(() => store.running || runBlockedReason.value !== null)
const runLabel = computed(() => (store.running ? `실행 중… ${elapsed.value}` : store.preview ? '다시 실행' : '미리보기 실행'))

function run(): void {
  if (!runDisabled.value) void store.runPreview()
}

function onImageCap(e: Event): void {
  const raw = (e.target as HTMLInputElement).value.trim()
  const v = Number(raw)
  store.imageCap = raw === '' || !Number.isFinite(v) || v <= 0 ? null : Math.floor(v)
}

// ---- 탐색기 ----

const badges = computed(() => tableBadges(store.preview))
const explorerNote = computed(() =>
  sandbox.status?.state === 'building' ? '기준선을 만드는 중이라 테이블 목록이 아직 없습니다.' : sandbox.schemaError,
)
const pickedTable = shallowRef<string | null>(null)
watch(
  () => store.selectedFiringId,
  () => (pickedTable.value = null),
)
const currentTable = computed(() => {
  if (pickedTable.value) return pickedTable.value
  const id = store.selectedFiringId
  const f = id ? store.preview?.firings.find((x) => x.id === id) : undefined
  return f?.table ?? store.preview?.statements[0]?.target ?? null
})

/** 테이블을 고르면 그 테이블의 첫 행 있는 묶음(표시 순번 기준)을 선택한다. */
function onSelectTable(table: string): void {
  const order = store.firingOrder
  const first = (store.preview?.firings ?? [])
    .filter((f) => f.table === table && f.rowCount > 0)
    .sort((a, b) => (order.get(a.id) ?? 0) - (order.get(b.id) ?? 0))[0]
  if (first) store.selectFiring(first.id)
  else pickedTable.value = table
}

// ---- 결과 탭 ----

const TABS: { id: ResultTab; label: string }[] = [
  { id: 'graph', label: '전파 그래프' },
  { id: 'rows', label: '행 변경' },
]

function onTabKey(e: KeyboardEvent): void {
  const i = TABS.findIndex((t) => t.id === store.resultTab)
  let next: number | null = null
  if (e.key === 'ArrowRight') next = (i + 1) % TABS.length
  else if (e.key === 'ArrowLeft') next = (i + TABS.length - 1) % TABS.length
  else if (e.key === 'Home') next = 0
  else if (e.key === 'End') next = TABS.length - 1
  if (next === null) return
  e.preventDefault()
  const id = TABS[next].id
  store.setResultTab(id)
  void nextTick(() => document.getElementById(`res-tab-${id}`)?.focus())
}

const PREVIEW_META: Record<PreviewStatus, string> = {
  ok: '미리보기 정상',
  failed: '문장 실패',
  trigger_blocked: '트리거 거부',
  truncated: '결과 잘림',
}
const APPLY_META: Record<ApplyStatus, string> = {
  committed: '적용 완료',
  rolled_back_mismatch: '불일치로 롤백',
  drift_detected: '드리프트 감지',
  precondition_failed: '적용 선행 조건 실패',
  error: '적용 오류',
}

/** 탭 오른쪽 실행 정보. 예: "실행 14:27 · 1.6초 · 미리보기 정상" */
const runMeta = computed(() => {
  if (store.running) return '미리보기를 실행하는 중…'
  if (store.applying) return '원본에 적용하는 중…'
  const p = store.preview
  if (!p) return store.runError ? '실행하지 못함' : '실행 안 함'
  const at = new Date(p.createdAt).toLocaleTimeString('ko-KR', { hour: '2-digit', minute: '2-digit', hour12: false })
  const ms = Object.values(p.timingsMs).reduce((a, b) => a + b, 0)
  const status = store.applyResult ? APPLY_META[store.applyResult.status] : PREVIEW_META[p.status]
  return `실행 ${at} · ${(ms / 1000).toFixed(1)}초 · ${status}`
})

/** 경과 시간은 매초 바뀌므로 live 영역(runMeta) 밖에서 따로 보인다. */
const runElapsed = computed(() => (store.running ? elapsed.value : ''))

/** 문장이 거부·실패했을 때 결과 영역 위에 띄우는 알림. */
const statementAlert = computed(() => {
  const p = store.preview
  if (!p || (p.status !== 'trigger_blocked' && p.status !== 'failed')) return null
  const s = p.statements.find((x) => x.status === p.status) ?? p.statements.find((x) => x.error)
  const code = s?.error && s.error.errorNumber !== 0 ? `(오류 ${s.error.errorNumber})` : ''
  return p.status === 'trigger_blocked'
    ? { title: `트리거가 문장을 거부했습니다${code}`, body: '원본에서도 같은 결과가 납니다. 변경된 행은 없습니다.' }
    : { title: `문장이 실패했습니다${code}`, body: s?.error?.message ?? '실패한 문장 뒤의 변경은 수집하지 않았습니다.' }
})

const runErrorTitle = computed(() =>
  store.runError?.status === 422 ? '거부된 문장이 있어 실행하지 않았습니다.' : '미리보기를 실행하지 못했습니다.',
)

/** 결과가 없을 때 결과 영역 안내. */
const emptyState = computed(() => {
  if (store.running)
    return {
      title: '미리보기를 실행하는 중',
      body: '샌드박스에서 실행하고 변경을 수집합니다. 끝날 때까지 기다리는 요청입니다. [중단]은 이 화면의 대기만 끊고, 서버는 끝까지 실행한 뒤 롤백합니다.',
    }
  const st = sandbox.status?.state
  if (st === 'building') return { title: '샌드박스 기준선을 만드는 중', body: '다 만들면 미리보기를 실행할 수 있습니다.' }
  if (st === 'dirty' || st === 'error')
    return { title: '샌드박스를 새로 고쳐야 합니다', body: '위 띠의 [샌드박스 새로 고침]으로 기준선을 다시 만든 뒤 실행하세요.' }
  return { title: '미리보기 결과 없음', body: '전파 그래프와 행 변경은 미리보기가 끝난 뒤에 채워집니다. SQL을 입력하고 [미리보기 실행](Ctrl+Enter)을 누르세요.' }
})

// ---- 레일 ----

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
const headline = computed(() => previewHeadline(store.preview))
const verdict = computed(() => applyStatusLine(gate.value))
</script>

<template>
  <div class="workbench">
    <SandboxBar :explorer-open="explorerOpen" @toggle-explorer="explorerOpen = !explorerOpen" />
    <ProblemBanner v-if="sandbox.noSession" :error="sandbox.noSession" title="이 창에서는 쓸 수 없습니다." class="nos" />
    <div class="work">
      <aside v-show="explorerOpen" id="explorer" class="explorer" :style="{ width: `${xSplit.size.value}px` }" aria-labelledby="h-ex">
        <h2 id="h-ex" class="sr-only">개체 탐색기</h2>
        <ExplorerTree
          :schema="sandbox.schema"
          :schema-error="explorerNote"
          :badges="badges"
          :current-table="currentTable"
          @select="onSelectTable"
          @hover="store.setHoveredTable"
        />
      </aside>
      <div
        v-show="explorerOpen"
        class="sep v"
        :class="{ on: xSplit.dragging.value }"
        aria-label="탐색기 폭 조절. 좌우 화살표 키로도 조절합니다"
        v-bind="xSplit.separatorAttrs.value"
      ></div>

      <main id="main" class="center" tabindex="-1">
        <section class="ed" :class="{ col: editorCollapsed }" :style="editorCollapsed ? undefined : { height: `${ySplit.size.value}px` }" aria-labelledby="h-ed">
          <h2 id="h-ed" class="sr-only">SQL 편집기</h2>
          <div class="tabrow">
            <div class="tablist" role="tablist" aria-label="편집기 탭">
              <button id="ed-tab-sql" type="button" role="tab" aria-selected="true" aria-controls="ed-body" tabindex="0">
                <span class="mono">쿼리 1.sql</span>
              </button>
            </div>
            <div class="tool">
              <label class="cap">
                이미지 상한
                <input
                  type="number"
                  min="1"
                  placeholder="기본"
                  :value="store.imageCap ?? ''"
                  :disabled="store.running || store.applying"
                  aria-describedby="cap-desc"
                  @input="onImageCap"
                />
                <span id="cap-desc" class="sr-only">행 이미지 수집 상한. 비우면 서버 기본값을 씁니다.</span>
              </label>
              <button type="button" class="btn btn-sm" :aria-expanded="!editorCollapsed" aria-controls="ed-body" @click="editorCollapsed = !editorCollapsed">
                {{ editorCollapsed ? '편집기 펼치기' : '편집기 접기' }}
              </button>
              <span v-if="runBlockedReason" id="run-why" class="hint why">{{ runBlockedReason }}</span>
              <button
                type="button"
                class="btn btn-sm"
                :class="{ 'btn-primary': !store.preview && !store.running }"
                :aria-disabled="runDisabled"
                :aria-describedby="runBlockedReason ? 'run-why' : undefined"
                aria-keyshortcuts="Control+Enter"
                @click="run"
              >
                {{ runLabel }} <span class="kb" aria-hidden="true">Ctrl+Enter</span>
              </button>
            </div>
          </div>
          <div id="ed-body" class="ed-body" role="tabpanel" aria-labelledby="ed-tab-sql" :inert="editorCollapsed">
            <div class="editor-box">
              <SqlEditor
                :model-value="store.sql"
                :schema="sandbox.schema"
                :diagnostics="store.diagnostics"
                :readonly="store.running || store.applying"
                @update:model-value="store.setSql"
                @run="run"
              />
            </div>
            <div class="diag-line" aria-label="분석 결과" :aria-busy="store.analyzing">
              <ProblemBanner v-if="store.analyzeError" :error="store.analyzeError" title="분석하지 못했습니다." class="note" />
              <DiagnosticList
                v-else-if="store.analysis || store.diagnostics.length"
                :items="store.diagnostics"
                :statements="store.analysis?.statements"
                empty-text="분석할 문장이 없습니다."
              />
              <p v-else class="hint note">{{ store.analyzing ? '분석하는 중…' : '입력을 멈추면 자동으로 분석합니다.' }}</p>
              <p v-if="store.applying" class="hint note" role="status">원본에 적용하는 중이라 미리보기·분석·편집을 잠시 막았습니다. 끝나면 풀립니다.</p>
            </div>
          </div>
        </section>

        <div
          class="sep h"
          :class="{ on: ySplit.dragging.value }"
          aria-label="편집기 높이 조절. 위아래 화살표 키로도 조절합니다"
          v-bind="ySplit.separatorAttrs.value"
        ></div>

        <section class="bottom" aria-labelledby="h-res" :aria-busy="store.running">
          <h2 id="h-res" class="sr-only">분석 결과</h2>
          <div class="tabrow">
            <div class="tablist" role="tablist" aria-label="결과 탭" @keydown="onTabKey">
              <button
                v-for="t in TABS"
                :id="`res-tab-${t.id}`"
                :key="t.id"
                type="button"
                role="tab"
                :aria-selected="store.resultTab === t.id"
                :aria-controls="store.preview ? `res-panel-${t.id}` : undefined"
                :tabindex="store.resultTab === t.id ? 0 : -1"
                @click="store.setResultTab(t.id)"
              >
                {{ t.label }}
              </button>
            </div>
            <div class="tool">
              <span class="hint run-meta"><span role="status">{{ runMeta }}</span><span v-if="runElapsed"> {{ runElapsed }}</span></span>
            </div>
          </div>

          <div class="res-body">
            <div class="res-main">
              <div v-if="store.runError || store.runAborted || statementAlert" class="res-notes">
                <ProblemBanner v-if="store.runError" :error="store.runError" :title="runErrorTitle" />
                <div v-else-if="store.runAborted" class="banner banner-warn" role="status">
                  <svg class="ic" viewBox="0 0 16 16" aria-hidden="true"><path d="M8 2.2L14.2 13H1.8z" /><path d="M8 6.6v3M8 11.4v.1" /></svg>
                  <span>
                    <b>대기를 중단했습니다</b>
                    서버에서 실행이 끝나기 전에는 다음 미리보기가 잠금(409 LOCKED)으로 거절될 수 있습니다.
                  </span>
                </div>
                <div v-if="statementAlert" class="banner banner-err" role="alert">
                  <svg class="ic" viewBox="0 0 16 16" aria-hidden="true"><circle cx="8" cy="8" r="5.8" /><path d="M4 12L12 4" /></svg>
                  <span><b>{{ statementAlert.title }}</b>{{ statementAlert.body }}</span>
                </div>
              </div>

              <div v-if="!store.preview" class="res-empty">
                <h3>{{ emptyState.title }}</h3>
                <p>{{ emptyState.body }}</p>
              </div>
              <template v-else>
                <div
                  id="res-panel-graph"
                  v-show="store.resultTab === 'graph'"
                  class="panel"
                  role="tabpanel"
                  aria-labelledby="res-tab-graph"
                >
                  <PropagationGraph
                    v-if="store.resultTab === 'graph'"
                    :graph="store.preview.graph"
                    :firings="store.preview.firings"
                    :selected-firing-id="store.selectedFiringId"
                    :show-zero-rows="store.showZeroRows"
                    :order="store.firingOrder"
                    :hovered-table="store.hoveredTable"
                    @select-firing="(id: string) => store.selectFiring(id, { openDrawer: true })"
                    @show-rows="store.setResultTab('rows')"
                    @update:show-zero-rows="(v: boolean) => (store.showZeroRows = v)"
                  />
                </div>
                <div id="res-panel-rows" v-show="store.resultTab === 'rows'" class="panel" role="tabpanel" aria-labelledby="res-tab-rows">
                  <DiffGrid
                    :preview="store.preview"
                    :selected-firing-id="store.selectedFiringId"
                    :view="store.view"
                    :show-zero-rows="store.showZeroRows"
                    :consumed="store.consumed"
                    @update:selected-firing-id="(id: string | null) => store.selectFiring(id)"
                    @update:view="(v) => (store.view = v)"
                  />
                </div>
              </template>
            </div>
            <!-- 서랍 자리(2단계): 선택한 묶음 상세. store.drawerOpen 과 selectedFiringId 로 연다. -->
            <aside v-if="store.drawerOpen && store.selectedFiringId" id="drawer" class="drawer" aria-label="선택한 묶음">
              <GraphDrawer />
            </aside>
          </div>
        </section>
        <component :is="FixtureNote" v-if="FixtureNote" />
      </main>

      <div class="sep v rail-sep" aria-hidden="true"></div>
      <aside class="rail" aria-labelledby="h-rail" :aria-busy="store.running || store.applying">
        <div class="rail-top">
          <div role="status"><h2 id="h-rail" class="headline">{{ headline }}</h2></div>
          <p class="verdict" :class="verdict.tone">
            <svg v-if="verdict.tone === 'ok'" class="ic" viewBox="0 0 16 16" aria-hidden="true"><path d="M3 8.5l3.2 3.2L13 4.8" /></svg>
            <svg v-else-if="verdict.tone === 'no'" class="ic" viewBox="0 0 16 16" aria-hidden="true"><path d="M4 4l8 8M12 4l-8 8" /></svg>
            <svg v-else class="ic" viewBox="0 0 16 16" aria-hidden="true"><circle cx="8" cy="8" r="5.8" /><path d="M8 7.4v3.6M8 5v.1" /></svg>
            <span>
              <span role="status">{{ verdict.text }}</span>
              <span v-if="verdict.remain"> · {{ verdict.remain }}</span>
            </span>
          </p>
        </div>
        <div v-if="store.preview?.status === 'truncated'" class="banner banner-warn rail-banner" role="alert">
          <svg class="ic" viewBox="0 0 16 16" aria-hidden="true"><path d="M8 2.2L14.2 13H1.8z" /><path d="M8 6.6v3M8 11.4v.1" /></svg>
          <span>
            <b>행 이미지 상한 초과 · 키만 수집</b>
            테이블별 건수는 정확합니다. 행 값은 상한({{ store.preview.capture.cap.toLocaleString('en-US') }})을 넘어 키만 남겼습니다.
          </span>
        </div>
        <div class="rail-body">
          <PreviewSummary v-if="store.preview" :preview="store.preview" :consumed="store.consumed" />
          <p v-else class="hint rail-empty">미리보기를 실행하면 테이블별 변경 행 수, 경고, 환경 지문, 수집 범위가 여기에 나옵니다.</p>
        </div>
        <div class="rail-apply">
          <ApplyPanel />
        </div>
      </aside>
    </div>
  </div>
</template>

<style scoped>
.workbench {
  display: flex;
  flex-direction: column;
  height: calc(100dvh - var(--menu-h));
  min-height: 560px;
}
.nos {
  flex: none;
  margin: 6px var(--gx) 0;
}
.work {
  flex: 1;
  min-height: 0;
  display: flex;
}

/* 분할 막대: 1px 선 + 양옆 4px 잡는 자리 */
.sep {
  position: relative;
  flex: none;
  background: var(--line);
  touch-action: none;
}
.sep.v {
  width: 1px;
  cursor: col-resize;
}
.sep.h {
  height: 1px;
  cursor: row-resize;
}
.sep::after {
  content: '';
  position: absolute;
}
.sep.v::after {
  inset: 0 -4px;
}
.sep.h::after {
  inset: -4px 0;
}
.sep[role='separator']:hover,
.sep:focus-visible,
.sep.on {
  background: var(--accent);
}
.sep:focus-visible {
  z-index: 2;
  outline-offset: 0;
}
.rail-sep {
  cursor: default;
}

.explorer {
  flex: none;
  display: flex;
  flex-direction: column;
  overflow: hidden;
  background: var(--panel);
}

.center {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  background: var(--panel);
  outline: none;
}

/* 편집기 */
.ed {
  flex: none;
  min-height: 68px;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}
.ed.col {
  height: 35px;
  min-height: 35px;
}
.ed-body {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
  overflow: auto;
  overscroll-behavior: contain;
}
.editor-box {
  flex: 1 1 66px;
  display: flex;
  min-height: 66px;
}
.editor-box > * {
  flex: 1;
  min-width: 0;
}
.diag-line {
  flex: none;
  border-top: 1px solid var(--line);
}
.diag-line .note {
  margin: 6px var(--gx);
}
.cap {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  color: var(--muted);
}
.cap input {
  width: 92px;
  min-height: 24px;
}
.why {
  max-width: 280px;
}

/* 결과 */
.bottom {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
}
.run-meta {
  font-size: 12px;
}
.res-body {
  flex: 1;
  min-height: 0;
  display: flex;
}
.res-main {
  flex: 1;
  min-width: 0;
  min-height: 0;
  display: flex;
  flex-direction: column;
  overflow: auto;
  overscroll-behavior: contain;
}
.res-notes {
  display: grid;
  gap: 8px;
  padding: 8px var(--gx) 0;
}
.panel {
  flex: 1;
  padding: 8px var(--gx) 12px;
}
.res-empty {
  max-width: 60ch;
  padding: 28px var(--gx);
  color: var(--muted);
}
.res-empty h3 {
  margin-bottom: 4px;
  color: var(--fg);
  font: 600 14px var(--ui);
}
.drawer {
  flex: none;
  width: 340px;
  overflow: auto;
  padding: 10px var(--gx) 14px;
  border-left: 1px solid var(--line);
  background: var(--panel);
}

/* 오른쪽 레일 */
.rail {
  flex: none;
  width: 344px;
  min-height: 0;
  display: flex;
  flex-direction: column;
  background: var(--panel);
}
.rail-top {
  flex: none;
  padding: 12px var(--gx) 10px;
  border-bottom: 1px solid var(--line);
}
.headline {
  font: 600 20px / 1.3 var(--ui);
}
.verdict {
  display: flex;
  gap: 6px;
  align-items: flex-start;
  margin-top: 8px;
  padding: 5px 8px;
  border: 1px solid;
  border-radius: var(--r);
  font-weight: 600;
}
.verdict .ic {
  margin-top: 3px;
}
.verdict.ok {
  color: var(--ok);
  background: var(--ok-bg);
  border-color: var(--ok);
}
.verdict.no {
  color: var(--err);
  background: var(--err-bg);
  border-color: var(--err);
}
.verdict.nil {
  color: var(--muted);
  background: var(--sunk);
  border-color: var(--ctl);
}
.rail-banner {
  margin: 10px var(--gx) 0;
}
.rail-body {
  flex: 1;
  min-height: 0;
  overflow: auto;
  overscroll-behavior: contain;
}
.rail-empty {
  padding: 10px var(--gx);
}
.rail-apply {
  flex: none;
  max-height: 55%;
  overflow: auto;
  border-top: 1px solid var(--tick);
  background: var(--panel);
}

@media (max-width: 999px) {
  .workbench {
    height: auto;
    min-height: 0;
  }
  .work {
    flex-direction: column;
  }
  .explorer {
    width: auto !important;
    max-height: 260px;
    border-bottom: 1px solid var(--line);
  }
  .sep.v {
    display: none;
  }
  .center {
    min-height: 620px;
  }
  .rail {
    width: auto;
    border-top: 1px solid var(--line);
  }
  .rail-body {
    overflow: visible;
  }
  .rail-apply {
    position: sticky;
    bottom: 0;
    z-index: 3;
    max-height: none;
  }
  .drawer {
    width: 280px;
  }
}
</style>
