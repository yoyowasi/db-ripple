<script setup lang="ts">
// 비교 화면(목업 docs/mockups/r2/3-compare.html, 2026-11-18 확정).
// 위: SQL 띠 | 적용 상자. 가운데: 바뀌는 테이블 비교표(합계 줄이 판정). 아래: 고른 테이블의 경로 한 줄 + 좌우 행 비교.
// 아래 창의 보조 보기로 발동 묶음(DiffGrid)과 전파 그래프(PropagationGraph)를 남긴다(기능을 잃지 않게).
// 같은 사실은 한 곳에서만: 거부는 결과 자리의 거부 칸 하나, 적용 결과는 결과 자리의 대조표 하나.
import { computed, defineAsyncComponent, nextTick, onMounted, ref, shallowRef, watch } from 'vue'
import type { ApplyStatus, PreviewStatus } from '../api/types'
import ApplyPanel from '../components/ApplyPanel.vue'
import ApplyResultView from '../components/ApplyResultView.vue'
import DiagnosticList from '../components/DiagnosticList.vue'
import DiffGrid from '../components/DiffGrid.vue'
import GraphDrawer from '../components/GraphDrawer.vue'
import NetChangeList from '../components/NetChangeList.vue'
import ProblemBanner from '../components/ProblemBanner.vue'
import PropagationGraph from '../components/PropagationGraph.vue'
import SandboxBar from '../components/SandboxBar.vue'
import SqlEditor from '../components/SqlEditor.vue'
import TableTree from '../components/compare/TableTree.vue'
import PreviewInfo from '../components/compare/PreviewInfo.vue'
import RowCompare from '../components/compare/RowCompare.vue'
import { splitTable } from '../components/rail/rowOps'
import { useNow } from '../composables/useNow'
import { useSplitter } from '../composables/useSplitter'
import { compareRows } from '../lib/compare'
import { formatCount, mmss } from '../lib/format'
import { previewHeadline } from '../lib/summary'
import type { ResultTab } from '../stores/preview'
import { usePreviewStore } from '../stores/preview'
import { useSandboxStore } from '../stores/sandbox'

// 개발 서버에서만: 시험 데이터 근거 표시. 운영 빌드에서는 빠진다.
const FixtureNote = import.meta.env.DEV ? defineAsyncComponent(() => import('../dev/FixtureNote.vue')) : null

const store = usePreviewStore()
const sandbox = useSandboxStore()
const now = useNow(1000)

const ySplit = useSplitter({ key: 'editor', axis: 'y', min: 44, max: 420, initial: 96 })
const editorCollapsed = shallowRef(false)
const editor = ref<InstanceType<typeof SqlEditor> | null>(null)

onMounted(() => {
  if (import.meta.env.DEV) void import('../dev/boot').then((m) => m.bootFixture())
  // 돌아왔을 때 현재 SQL을 한 번 분석해 둔다.
  if (store.sql.trim() && !store.analysis) void store.analyzeNow()
})

// ---- SQL 띠 ----

const elapsed = computed(() => (store.runStartedAt ? mmss(now.value - store.runStartedAt) : ''))
const rejected = computed(() => !!store.analysis && !store.analysis.isAccepted && !store.analyzing)

/** 실행할 수 없는 이유(실행 중이면 null: 단추가 진행 문구를 보인다). */
const runBlockedReason = computed<string | null>(() => {
  if (store.running) return null
  if (store.applying) return '원본에 적용하는 중이라 실행할 수 없습니다.'
  if (!store.sql.trim()) return 'SQL을 입력하세요.'
  if (rejected.value) return '거부된 문장이 있어 실행할 수 없습니다.'
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

const PREVIEW_META: Record<PreviewStatus, string> = {
  ok: '미리보기 정상',
  failed: '문장 실패',
  trigger_blocked: '트리거 거부',
  truncated: '결과 잘림',
}
const APPLY_META: Record<ApplyStatus, string> = {
  committed: '원본에 적용됨',
  rolled_back_mismatch: '불일치로 롤백',
  drift_detected: '드리프트 감지',
  precondition_failed: '적용 선행 조건 실패',
  error: '적용 오류',
}

/** SQL 띠 오른쪽 실행 정보. 예: "미리보기 정상 · 1.2초" */
const runMeta = computed(() => {
  if (store.running) return '미리보기를 실행하는 중…'
  if (store.applying) return '원본에 적용하는 중…'
  const p = store.preview
  if (!p) return store.runError ? '실행하지 못함' : '실행 안 함'
  if (store.applyResult) return APPLY_META[store.applyResult.status]
  const ms = Object.values(p.timingsMs).reduce((a, b) => a + b, 0)
  return `${PREVIEW_META[p.status]} · ${(ms / 1000).toFixed(1)}초`
})

// ---- 진단 자리: 미리보기가 없고 거부됐으면 결과 자리의 거부 칸 하나로, 그 밖에는 편집기 아래 한 줄 ----

const rejectView = computed(
  () => !store.preview && !store.running && (rejected.value || store.runError?.status === 422) && store.diagnostics.length > 0,
)
const firstError = computed(() => store.diagnostics.find((d) => d.severity === 'error') ?? null)
/** 미리보기가 있고 SQL이 그대로면 같은 경고를 알림 칸(preview.warnings)이 말하므로 편집기 아래에는 거부나 바뀐 SQL의 진단만 둔다. */
const diagUnderEditor = computed(() => {
  if (rejectView.value) return false
  if (store.analyzeError) return true
  if (store.diagnostics.length === 0) return false
  const sameAsPreview = !!store.preview && store.sql === store.previewSql
  return !sameAsPreview || store.diagnostics.some((d) => d.severity === 'error')
})

function gotoError(): void {
  const d = firstError.value
  if (!d) return
  editorCollapsed.value = false
  void nextTick(() => editor.value?.focusAt(d.span.startLine, d.span.startColumn))
}

// ---- 알림(있을 때만) ----

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
const runErrorTitle = computed(() => '미리보기를 실행하지 못했습니다.')
/** 422(거부)는 거부 칸이, 409 BUILDING(기준선 만드는 중)은 빈 결과 칸이 이미 말하므로 알림을 따로 띄우지 않는다. */
const showRunError = computed(() => {
  const e = store.runError
  if (!e) return false
  if (e.status === 422) return !rejectView.value
  return !(e.status === 409 && e.reason === 'BUILDING' && sandbox.status?.state === 'building')
})
/** 실행 단추가 꺼진 이유를 결과 칸이 이미 크게 말하면 단추 옆 글자는 화면 읽기 프로그램용으로만 남긴다. */
const whyHidden = computed(() => rejectView.value || (!store.preview && !store.running && sandbox.status?.state === 'building'))
/** 원본에서 실제로 실행을 시도한 적용(커밋·드리프트·불일치 롤백). 이때만 결과 자리를 대조표로 바꾼다. */
const attempted = computed(() => {
  const s = store.applyResult?.status
  return s === 'committed' || s === 'drift_detected' || s === 'rolled_back_mismatch'
})
const hasNotes = computed(() => {
  const p = store.preview
  return (
    (!!store.applyResult && !attempted.value) ||
    showRunError.value ||
    store.runAborted ||
    !!statementAlert.value ||
    (!!p && (p.status === 'truncated' || p.residualCheckFailed || p.residualCommitted.length > 0 || p.warnings.length > 0))
  )
})

// ---- 비교표·아래 창 ----

const rows = computed(() => (store.preview ? compareRows(store.preview) : []))
const curTable = computed(() => {
  const t = store.selectedTable
  if (t && rows.value.some((r) => r.table === t)) return t
  return rows.value[0]?.table ?? null
})
const curRow = computed(() => rows.value.find((r) => r.table === curTable.value) ?? null)
const curName = computed(() => (curTable.value ? splitTable(curTable.value) : null))
const schemaTable = computed(() => (curTable.value ? sandbox.findTable(curTable.value) : undefined))
const headline = computed(() => previewHeadline(store.preview))

const TABS: { id: ResultTab; label: string }[] = [
  { id: 'compare', label: '행 비교' },
  { id: 'rows', label: '발동 묶음' },
  { id: 'graph', label: '전파 그래프' },
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

/** 트리에서 테이블을 고르면 그 테이블의 첫 행 있는 묶음(표시 순번)도 고른다: 발동 묶음·그래프 선택이 같은 테이블을 따라가게. */
function pickTable(t: string): void {
  const order = store.firingOrder
  const first = (store.preview?.firings ?? [])
    .filter((f) => f.table === t && f.rowCount > 0)
    .sort((a, b) => (order.get(a.id) ?? 0) - (order.get(b.id) ?? 0))[0]
  if (first) store.selectFiring(first.id)
  else store.selectTable(t)
}

// 새 미리보기가 오면 아래 창은 행 비교부터 보인다.
watch(
  () => store.preview?.previewId,
  (id) => {
    if (id) store.setResultTab('compare')
  },
)

/** 결과가 없을 때 안내 */
const emptyState = computed(() => {
  if (store.running)
    return {
      title: '미리보기를 실행하는 중',
      body: '샌드박스에서 실행하고 변경을 수집합니다. [중단]은 이 화면의 대기만 끊고, 서버는 끝까지 실행한 뒤 롤백합니다.',
    }
  const st = sandbox.status?.state
  if (st === 'building') return { title: '샌드박스 기준선을 만드는 중', body: '다 만들면 미리보기를 실행할 수 있습니다.' }
  if (st === 'dirty' || st === 'error')
    return { title: '샌드박스를 새로 고쳐야 합니다', body: '위 띠의 샌드박스 상태를 눌러 [샌드박스 새로 고침]으로 기준선을 다시 만든 뒤 실행하세요.' }
  return { title: '미리보기 결과 없음', body: 'SQL을 쓰고 [미리보기 실행](Ctrl+Enter)을 누르면 바뀌는 테이블과 행이 여기에 나옵니다.' }
})
</script>

<template>
  <div class="cmpapp">
    <SandboxBar />
    <ProblemBanner v-if="sandbox.noSession" :error="sandbox.noSession" title="이 창에서는 쓸 수 없습니다." class="nos" />
    <main id="main" class="body" tabindex="-1">
      <div class="band">
        <section class="box sqlbox" aria-labelledby="h-ed">
          <h2 id="h-ed" class="sr-only">SQL 편집기</h2>
          <div class="bar">
            <button type="button" class="tgl" :aria-expanded="!editorCollapsed" aria-controls="ed-body" @click="editorCollapsed = !editorCollapsed">
              <svg class="ic" viewBox="0 0 16 16" aria-hidden="true"><path :d="editorCollapsed ? 'M6 4l4 4-4 4' : 'M4 6l4 4 4-4'" /></svg>
              SQL
            </button>
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
            <!-- 거부 칸이 이유를 말할 때는 같은 문장을 화면에 두 번 쓰지 않는다(화면 읽기 프로그램용으로만 남김) -->
            <span v-if="runBlockedReason" id="run-why" class="hint why" :class="{ 'sr-only': whyHidden }">{{ runBlockedReason }}</span>
            <span class="sp"></span>
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
            <details v-if="store.preview && !store.running" class="pinfo">
              <summary>
                <span role="status">{{ runMeta }}</span>
                <svg class="ic" viewBox="0 0 16 16" aria-hidden="true"><path d="M4 6l4 4 4-4" /></svg>
              </summary>
              <div class="pop"><PreviewInfo :preview="store.preview" :consumed="store.consumed" /></div>
            </details>
            <span v-else class="hint meta"><span role="status">{{ runMeta }}</span></span>
          </div>
          <div v-show="!editorCollapsed" id="ed-body" class="ed-body" :style="{ height: `${ySplit.size.value}px` }">
            <SqlEditor
              ref="editor"
              :model-value="store.sql"
              :schema="sandbox.schema"
              :diagnostics="store.diagnostics"
              :readonly="store.running || store.applying"
              :engine="sandbox.status?.engine"
              @update:model-value="store.setSql"
              @run="run"
            />
          </div>
          <div
            v-show="!editorCollapsed"
            class="sep h"
            :class="{ on: ySplit.dragging.value }"
            aria-label="편집기 높이 조절. 위아래 화살표 키로도 조절합니다"
            v-bind="ySplit.separatorAttrs.value"
          ></div>
          <div v-if="diagUnderEditor" class="diag-line" aria-label="분석 결과" :aria-busy="store.analyzing">
            <ProblemBanner v-if="store.analyzeError" :error="store.analyzeError" title="분석하지 못했습니다." class="note" />
            <DiagnosticList v-else :items="store.diagnostics" />
          </div>
        </section>
        <aside class="box applybox" aria-label="원본에 적용">
          <ApplyPanel />
        </aside>
      </div>

      <div v-if="hasNotes" class="notes">
        <ProblemBanner v-if="showRunError && store.runError" :error="store.runError" :title="runErrorTitle" />
        <div v-if="store.runAborted" class="banner banner-warn" role="status">
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
        <!-- 실행 전에 멈춘 적용(선행 조건 실패·오류): 미리보기는 그대로 유효하므로 비교 화면을 두고 결과만 여기에 -->
        <ApplyResultView v-if="store.applyResult && !attempted && store.preview" :result="store.applyResult" :preview="store.preview" />
        <template v-if="store.preview">
          <div v-if="store.preview.status === 'truncated'" class="banner banner-warn" role="alert">
            <svg class="ic" viewBox="0 0 16 16" aria-hidden="true"><path d="M8 2.2L14.2 13H1.8z" /><path d="M8 6.6v3M8 11.4v.1" /></svg>
            <span>
              <b>행 이미지 상한 초과 · 키만 수집</b>
              테이블별 건수는 정확합니다. 행 값은 상한({{ store.preview.capture.cap.toLocaleString('en-US') }})을 넘어 키만 남겼습니다.
            </span>
          </div>
          <div v-if="store.preview.residualCheckFailed" class="banner banner-err" role="alert">
            <svg class="ic" viewBox="0 0 16 16" aria-hidden="true"><circle cx="8" cy="8" r="5.8" /><path d="M4 12L12 4" /></svg>
            <span>
              <b>잔여 확인 실패</b>
              롤백 뒤 남은 변경을 확인하지 못했습니다. 남은 변경이 없다고 말할 수 없고 샌드박스는 오염 상태가 됩니다.
              <template v-if="store.preview.residualCheckMessage"> 사유: {{ store.preview.residualCheckMessage }}</template>
            </span>
          </div>
          <div v-if="store.preview.residualCommitted.length" class="banner banner-warn">
            <svg class="ic" viewBox="0 0 16 16" aria-hidden="true"><path d="M8 2.2L14.2 13H1.8z" /><path d="M8 6.6v3M8 11.4v.1" /></svg>
            <div>
              <b>롤백 뒤 남은 변경 {{ store.preview.residualCommitted.length }}건</b>
              트리거가 롤백 뒤에 남긴 변경입니다. 샌드박스는 오염으로 표시되고 다시 만들어집니다.
              <NetChangeList :items="store.preview.residualCommitted" />
            </div>
          </div>
          <div v-if="store.preview.warnings.length" class="banner banner-warn">
            <svg class="ic" viewBox="0 0 16 16" aria-hidden="true"><path d="M8 2.2L14.2 13H1.8z" /><path d="M8 6.6v3M8 11.4v.1" /></svg>
            <div>
              <b>경고 {{ store.preview.warnings.length }}건</b>
              <DiagnosticList :items="store.preview.warnings" />
            </div>
          </div>
        </template>
      </div>

      <section class="box result" aria-labelledby="h-res" :aria-busy="store.running">
        <h2 id="h-res" class="sr-only">결과</h2>
        <p class="sr-only" role="status">{{ store.preview ? headline : '' }}</p>

        <!-- 1) 적용 결과: 같은 자리를 "미리보기 → 실제" 대조표로 바꾼다 -->
        <div v-if="attempted && store.applyResult && store.preview" class="appres">
          <ApplyResultView :result="store.applyResult" :preview="store.preview" />
        </div>

        <!-- 2) 미리보기: 테이블 단위 원인 트리(D안, 2026-11-18 확정) + 아래 창. 원인 경로는 트리가 보이므로 아래 창은 행만 -->
        <template v-else-if="store.preview">
          <TableTree
            class="ctable"
            :class="{ short: store.resultTab !== 'compare' }"
            :preview="store.preview"
            :selected="curTable"
            @select="pickTable"
          />
          <div class="pane">
            <div class="phead">
              <div class="ptitle">
                <h3 v-if="curName">
                  <span class="sc">{{ curName.schema }}.</span>{{ curName.name }}
                  <small v-if="curRow && curRow.firingRows > curRow.total">
                    묶음 {{ curRow.firings }}개가 같은 행을 바꿔 순변경은 {{ formatCount(curRow.total) }}행
                  </small>
                </h3>
                <h3 v-else>바뀐 행 없음</h3>
              </div>
              <div class="tablist" role="tablist" aria-label="아래 창 보기" @keydown="onTabKey">
                <button
                  v-for="t in TABS"
                  :id="`res-tab-${t.id}`"
                  :key="t.id"
                  type="button"
                  role="tab"
                  :aria-selected="store.resultTab === t.id"
                  :aria-controls="store.resultTab === t.id ? `res-panel-${t.id}` : undefined"
                  :tabindex="store.resultTab === t.id ? 0 : -1"
                  @click="store.setResultTab(t.id)"
                >
                  {{ t.label }}
                </button>
              </div>
            </div>
            <div class="pbody">
              <div v-if="store.resultTab === 'compare'" id="res-panel-compare" class="panel" role="tabpanel" aria-labelledby="res-tab-compare">
                <!-- key: 테이블이 바뀌면 새로 만든다(서버에서 지워진 뒤 이전 테이블의 행이 새 이름 밑에 남지 않게) -->
                <RowCompare
                  v-if="curTable"
                  :key="curTable"
                  :preview-id="store.preview.previewId"
                  :table="curTable"
                  :consumed="store.consumed"
                  :schema-table="schemaTable"
                  :origin-db="store.preview.baseline.originDb"
                />
                <p v-else class="hint pad">바뀐 행이 없습니다. 문장과 트리거의 흐름은 [전파 그래프]에서 볼 수 있습니다.</p>
              </div>
              <div v-else-if="store.resultTab === 'rows'" id="res-panel-rows" class="panel pad" role="tabpanel" aria-labelledby="res-tab-rows">
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
              <div v-else id="res-panel-graph" class="panel graph" role="tabpanel" aria-labelledby="res-tab-graph">
                <div class="gmain">
                  <PropagationGraph
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
                <aside v-if="store.drawerOpen && store.selectedFiringId" id="drawer" class="drawer" aria-label="선택한 묶음">
                  <GraphDrawer />
                </aside>
              </div>
            </div>
          </div>
        </template>

        <!-- 3) 거부: 사실을 한 곳에서, 고칠 위치와 함께 -->
        <div v-else-if="rejectView" class="reject" role="alert">
          <h3 v-if="firstError">
            <svg class="ic" viewBox="0 0 16 16" aria-hidden="true"><circle cx="8" cy="8" r="5.8" /><path d="M4 12L12 4" /></svg>
            {{ firstError.span.startLine }}행 · {{ firstError.ruleId }} — 실행하지 않았습니다
          </h3>
          <p v-if="firstError" class="msg">{{ firstError.message }}</p>
          <DiagnosticList :items="store.diagnostics" :statements="store.analysis?.statements" />
          <button v-if="firstError" type="button" class="btn btn-sm" @click="gotoError">{{ firstError.span.startLine }}행으로 이동</button>
        </div>

        <!-- 4) 아직 결과 없음 -->
        <div v-else class="empty">
          <h3>{{ emptyState.title }}<template v-if="store.running"> · {{ elapsed }}</template></h3>
          <p>{{ emptyState.body }}</p>
        </div>
      </section>
      <component :is="FixtureNote" v-if="FixtureNote" />
    </main>
  </div>
</template>

<style scoped>
.cmpapp {
  display: flex;
  flex-direction: column;
  height: calc(100dvh - var(--menu-h));
  min-height: 560px;
}
.nos {
  flex: none;
  margin: 8px 16px 0;
}
.body {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 12px 16px;
  outline: none;
}
.box {
  min-width: 0;
  border: 1px solid var(--line);
  border-radius: 6px;
  background: var(--panel);
}

/* 위: SQL 띠 | 적용 상자 */
.band {
  flex: none;
  display: grid;
  grid-template-columns: minmax(0, 1fr) 320px;
  gap: 12px;
  align-items: start;
}
.sqlbox {
  display: flex;
  flex-direction: column;
  overflow: hidden;
}
.bar {
  display: flex;
  align-items: center;
  gap: 10px;
  min-height: 38px;
  padding: 4px 10px;
  border-bottom: 1px solid var(--line);
}
.tgl {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 6px;
  border: 1px solid transparent;
  border-radius: var(--r);
  background: none;
  color: var(--muted);
  font: 600 12px var(--ui);
  cursor: pointer;
}
.tgl:hover {
  border-color: var(--line);
  color: var(--fg);
}
.sp {
  flex: 1;
}
.why {
  max-width: 260px;
  font-size: 12px;
}
.cap {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  color: var(--muted);
}
.cap input {
  width: 84px;
  min-height: 24px;
}
.meta {
  font-size: 12px;
}
.pinfo {
  position: relative;
}
.pinfo > summary {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 6px;
  border-radius: var(--r);
  color: var(--muted);
  font-size: 12px;
  list-style: none;
  cursor: pointer;
}
.pinfo > summary::-webkit-details-marker {
  display: none;
}
.pinfo > summary:hover {
  background: var(--sunk);
  color: var(--fg);
}
.pinfo .pop {
  position: absolute;
  top: calc(100% + 6px);
  right: 0;
  z-index: 30;
  width: min(460px, calc(100vw - 32px));
  max-height: 60dvh;
  overflow: auto;
  padding: 12px 14px;
  border: 1px solid var(--ctl);
  border-radius: 6px;
  background: var(--panel);
  box-shadow: 0 8px 24px rgb(0 0 0 / 0.18);
}
.ed-body {
  display: flex;
  min-height: 44px;
}
.ed-body > * {
  flex: 1;
  min-width: 0;
}
.sep {
  position: relative;
  flex: none;
  height: 1px;
  background: var(--line);
  cursor: row-resize;
  touch-action: none;
}
.sep::after {
  content: '';
  position: absolute;
  inset: -4px 0;
}
.sep:hover,
.sep:focus-visible,
.sep.on {
  background: var(--accent);
}
.diag-line .note,
.diag-line :deep(.diag),
.diag-line :deep(.empty) {
  margin: 6px 10px;
}
.applybox {
  border-top: 3px solid var(--accent);
}

/* 알림 */
.notes {
  flex: none;
  display: grid;
  gap: 8px;
  /* 알림이 길어도(적용 선행 조건 실패 결과 등) 원인 트리를 밀어내지 않게 칸 안에서 스크롤 */
  max-height: 34vh;
  overflow: auto;
  overscroll-behavior: contain;
}

/* 결과 */
.result {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}
.ctable {
  flex: 0 1 auto;
  max-height: 62%;
  border-bottom: 1px solid var(--line);
}
/* 보조 보기(발동 묶음·전파 그래프)는 넓은 자리가 필요해 비교표를 줄인다(표 안에서 스크롤) */
.ctable.short {
  max-height: 26%;
}
.pane {
  flex: 1;
  min-height: 180px;
  display: flex;
  flex-direction: column;
}
.phead {
  flex: none;
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 16px;
  padding: 10px 14px 8px;
  border-bottom: 1px solid var(--line);
}
.ptitle {
  display: grid;
  gap: 6px;
  min-width: 0;
}
.ptitle h3 {
  font: 700 16px / 1.3 var(--ui);
  letter-spacing: -0.01em;
}
.ptitle h3 .sc {
  color: var(--muted);
  font-weight: 400;
}
.ptitle h3 small {
  margin-left: 10px;
  color: var(--muted);
  font: 400 12px var(--ui);
}
.phead .tablist {
  flex: none;
}
.pbody {
  flex: 1;
  min-height: 0;
  overflow: auto;
  overscroll-behavior: contain;
}
.panel {
  min-height: 100%;
}
.pad {
  padding: 10px 14px;
}
.panel.graph {
  display: flex;
  height: 100%;
}
.gmain {
  flex: 1;
  min-width: 0;
  padding: 8px 14px 12px;
}
.drawer {
  flex: none;
  width: 340px;
  overflow: auto;
  padding: 10px 14px 14px;
  border-left: 1px solid var(--line);
}
.appres {
  flex: 1;
  min-height: 0;
  overflow: auto;
  padding: 4px 0;
}
.reject {
  display: grid;
  gap: 10px;
  justify-items: stretch;
  max-width: 820px;
  padding: 18px 20px;
  border-left: 4px solid var(--err);
}
.reject h3 {
  display: flex;
  gap: 8px;
  align-items: center;
  color: var(--err);
  font: 700 17px / 1.3 var(--ui);
}
.reject .msg {
  font-size: 14px;
}
.reject > .btn {
  justify-self: start;
}
.empty {
  max-width: 64ch;
  padding: 28px 20px;
  color: var(--muted);
}
.empty h3 {
  margin-bottom: 4px;
  color: var(--fg);
  font: 600 15px var(--ui);
}

@media (max-width: 999px) {
  .cmpapp {
    height: auto;
    min-height: 0;
  }
  .band {
    grid-template-columns: minmax(0, 1fr);
  }
  .result {
    min-height: 620px;
  }
  .drawer {
    width: 280px;
  }
}
</style>
