<script setup lang="ts">
// 미리보기 화면: C1 편집기 → C2 그래프 → C3 그리드, 오른쪽에 C4 요약·적용.
import { computed, onMounted } from 'vue'
import ApplyPanel from '../components/ApplyPanel.vue'
import DiagnosticList from '../components/DiagnosticList.vue'
import DiffGrid from '../components/DiffGrid.vue'
import PreviewSummary from '../components/PreviewSummary.vue'
import ProblemBanner from '../components/ProblemBanner.vue'
import PropagationGraph from '../components/PropagationGraph.vue'
import SqlEditor from '../components/SqlEditor.vue'
import { useNow } from '../composables/useNow'
import { mmss } from '../lib/format'
import { SAMPLE_SQL, usePreviewStore } from '../stores/preview'
import { useSandboxStore } from '../stores/sandbox'

const store = usePreviewStore()
const sandbox = useSandboxStore()
const now = useNow(1000)

onMounted(() => {
  // 돌아왔을 때 현재 SQL을 한 번 분석해 둔다.
  if (store.sql.trim() && !store.analysis) void store.analyzeNow()
})

const elapsed = computed(() => (store.runStartedAt ? mmss(now.value - store.runStartedAt) : ''))
const rejected = computed(() => !!store.analysis && !store.analysis.isAccepted && !store.analyzing)
const sandboxNotReady = computed(() => sandbox.status?.state === 'building')
// 적용 중에는 미리보기·분석·편집을 막는다(미리보기가 clearResult()로 적용 중인 결과를 지우지 못하게).
const busyApplying = computed(() => store.applying)
const canRun = computed(
  () => !store.running && !store.applying && store.sql.trim().length > 0 && !rejected.value && !sandboxNotReady.value,
)

const counts = computed(() => {
  const a = store.analysis
  if (!a) return null
  return { rejects: a.rejects.length, warnings: a.warnings.length }
})

function run(): void {
  if (canRun.value) void store.runPreview()
}
function analyze(): void {
  if (!store.applying) void store.analyzeNow()
}

const schemaInfo = computed(() => {
  if (sandbox.schema) return `자동완성: 테이블 ${sandbox.schema.tables.length}개 (기준선 ${sandbox.schema.baselineAt})`
  return sandbox.schemaError ? `자동완성을 쓸 수 없습니다: ${sandbox.schemaError}` : '자동완성 스키마를 불러오는 중…'
})

function onImageCap(e: Event): void {
  const raw = (e.target as HTMLInputElement).value.trim()
  const n = Number(raw)
  store.imageCap = raw === '' || !Number.isFinite(n) || n <= 0 ? null : Math.floor(n)
}
</script>

<template>
  <div class="home">
    <div class="col-main">
      <!-- C1 편집기 -->
      <section class="card" aria-label="쿼리 편집기">
        <header>
          <h2>SQL</h2>
          <label class="cap">
            이미지 상한
            <input
              type="number"
              min="1"
              placeholder="기본"
              :value="store.imageCap ?? ''"
              :disabled="store.running || busyApplying"
              aria-label="행 이미지 수집 상한(비우면 서버 기본값)"
              @input="onImageCap"
            />
          </label>
          <button type="button" class="btn btn-sm" :disabled="store.running || busyApplying" @click="store.setSql(SAMPLE_SQL)">예시 넣기</button>
          <button type="button" class="btn btn-sm" :disabled="store.analyzing || busyApplying || !store.sql.trim()" @click="analyze">
            {{ store.analyzing ? '분석 중…' : '분석' }}
          </button>
          <button type="button" class="btn btn-primary" :disabled="!canRun" @click="run">
            {{ store.running ? '미리보기 실행 중…' : '미리보기 (Ctrl+Enter)' }}
          </button>
        </header>
        <div class="editor-box">
          <SqlEditor
            :model-value="store.sql"
            :schema="sandbox.schema"
            :diagnostics="store.diagnostics"
            :readonly="store.running || busyApplying"
            @update:model-value="store.setSql"
            @run="run"
          />
        </div>
        <div class="card-body analysis">
          <div class="line">
            <span v-if="counts" class="chip" :class="store.analysis?.isAccepted ? 'chip-ok' : 'chip-err'">
              {{ store.analysis?.isAccepted ? '분석: 미리보기 가능' : `분석: 거부 ${counts.rejects}건` }}
            </span>
            <span v-if="counts && counts.warnings > 0" class="chip chip-warn">경고 {{ counts.warnings }}건</span>
            <span class="muted schema-info">{{ schemaInfo }}</span>
          </div>
          <ul v-if="store.analysis && store.analysis.statements.length" class="stmts">
            <li v-for="s in store.analysis.statements" :key="s.index">
              <span class="muted">#{{ s.index }}</span> {{ s.kind }} <strong>{{ s.target }}</strong>
              <span v-if="s.nondeterministicColumns.length" class="chip" title="문장 안 비결정 함수 등으로 값이 달라질 수 있는 열">
                예상값 열: {{ s.nondeterministicColumns.join(', ') }}
              </span>
            </li>
          </ul>
          <ProblemBanner v-if="store.analyzeError" :error="store.analyzeError" title="분석하지 못했습니다." />
          <DiagnosticList v-if="store.diagnostics.length" :items="store.diagnostics" />
          <p v-if="busyApplying" class="muted" role="status">원본에 적용하는 중이라 미리보기·분석·편집을 잠시 막았습니다. 끝나면 풀립니다.</p>
          <p v-else-if="sandboxNotReady" class="muted">기준선을 만드는 중이라 미리보기를 시작할 수 없습니다.</p>
        </div>
      </section>

      <!-- 실행 상태·오류 -->
      <div v-if="store.running" class="banner banner-info" role="status">
        <div>
          <strong>미리보기 실행 중… {{ elapsed }}</strong>
          샌드박스에서 실행하고 변경을 수집합니다. 끝날 때까지 기다리는 요청입니다(서버 쪽 제한 시간이 있습니다).
          [중단]은 이 화면의 대기만 끊습니다. 서버는 끝까지 실행한 뒤 롤백합니다.
        </div>
      </div>
      <div v-else-if="store.runAborted" class="banner banner-warn" role="status">
        <div>대기를 중단했습니다. 서버에서 실행이 끝나기 전에는 다음 미리보기가 잠금(409 LOCKED)으로 거절될 수 있습니다.</div>
      </div>
      <ProblemBanner v-if="store.runError" :error="store.runError" title="미리보기를 실행하지 못했습니다." />

      <!-- C2 그래프 -->
      <PropagationGraph
        :graph="store.preview?.graph ?? null"
        :firings="store.preview?.firings ?? []"
        :selected-firing-id="store.selectedFiringId"
        :show-zero-rows="store.showZeroRows"
        @select-firing="(id: string) => (store.selectedFiringId = id)"
        @update:show-zero-rows="(v: boolean) => (store.showZeroRows = v)"
      />

      <!-- C3 그리드 -->
      <DiffGrid
        :preview="store.preview"
        :selected-firing-id="store.selectedFiringId"
        :view="store.view"
        :show-zero-rows="store.showZeroRows"
        :consumed="store.consumed"
        @update:selected-firing-id="(id: string | null) => (store.selectedFiringId = id)"
        @update:view="(v) => (store.view = v)"
      />
    </div>

    <aside class="col-side" aria-label="요약과 적용">
      <PreviewSummary v-if="store.preview" :preview="store.preview" :consumed="store.consumed" />
      <section v-else class="card">
        <header><h2>요약</h2></header>
        <div class="card-body muted">미리보기를 실행하면 문장별 결과, 테이블별 건수, 경고, 환경 지문, 수집 범위가 여기에 나옵니다.</div>
      </section>
      <ApplyPanel />
    </aside>
  </div>
</template>

<style scoped>
.home {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 420px;
  gap: var(--gutter);
  align-items: start;
}
.col-main,
.col-side {
  display: grid;
  gap: var(--gutter);
  min-width: 0;
}
.col-side {
  position: sticky;
  top: var(--gutter);
  max-height: calc(100vh - 2 * var(--gutter));
  overflow: auto;
}
.cap {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 12.5px;
}
.cap input {
  width: 96px;
}
.editor-box {
  height: 200px;
  border-bottom: 1px solid var(--border);
}
.analysis {
  display: grid;
  gap: 8px;
}
.line {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}
.schema-info {
  font-size: 12px;
}
.stmts {
  list-style: none;
  margin: 0;
  padding: 0;
  display: grid;
  gap: 2px;
  font-size: 12.5px;
}

@media (max-width: 1100px) {
  .home {
    grid-template-columns: minmax(0, 1fr);
  }
  .col-side {
    position: static;
    max-height: none;
    overflow: visible;
  }
}
</style>
