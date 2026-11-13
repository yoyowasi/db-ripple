<script setup lang="ts">
// 선택한 묶음 상세 서랍(목업 v6). 제목·원인·확신도·플래그와 이 묶음 행 미리보기(처음 20행).
// 서랍을 여는 동작(store.drawerFocusTick)일 때만 제목으로 포커스한다. 다른 곳에서 묶음만 바꿀 때는 포커스를 건드리지 않는다.
// Esc·[닫기]로 닫으면 그래프의 그 노드, 없으면 열기 전 포커스 요소, 그것도 없으면 결과 탭으로 돌려준다.
import { computed, nextTick, onBeforeUnmount, onMounted, shallowRef, useTemplateRef, watch } from 'vue'
import { getRows, isAbortError } from '../api/client'
import type { Firing, RowChange, RowPage } from '../api/types'
import { CONFIDENCE_LABEL, OP_LABEL, keyText } from '../lib/format'
import { FK_ACTION_TEXT, OBSERVED_ONLY_TEXT } from '../lib/graphStyle'
import { CAUSE_LABEL } from '../lib/summary'
import { usePreviewStore } from '../stores/preview'

const PAGE = 20
const store = usePreviewStore()

const firing = computed<Firing | null>(() => store.preview?.firings.find((f) => f.id === store.selectedFiringId) ?? null)
const order = computed(() => (firing.value ? (store.firingOrder.get(firing.value.id) ?? null) : null))
const graphNodeId = computed(() => {
  const f = firing.value
  const n = f && store.preview?.graph.nodes.find((x) => x.type === 'firing' && x.firingId === f.id)
  return n ? n.id : null
})

const fmtN = (v: number) => v.toLocaleString('en-US')
const opText = computed(() => {
  const f = firing.value
  if (!f) return ''
  return f.rowCount === 0 ? `${OP_LABEL[f.operation]} 0행(변경 없음)` : `${fmtN(f.rowCount)}행 ${OP_LABEL[f.operation]}`
})
const flags = computed(() => {
  const f = firing.value
  if (!f) return []
  const out: { text: string; title: string }[] = []
  if (f.imageMode === 'KEYS') out.push({ text: '키만 수집', title: '행 이미지 없이 키만 수집함(KEYS). 행 값은 보이지 않을 수 있습니다.' })
  if (f.auditOrder === 'NONE') out.push({ text: '감사 순서 불확실', title: '이 테이블에 사용자 Last 트리거가 있어 감사 트리거 순서를 보장하지 못함(NONE). 자식 귀속이 모호할 수 있습니다.' })
  if (f.rowCount === 0) out.push({ text: '0행', title: '발동했지만 바뀐 행이 없습니다.' })
  if (f.orderGuarantee === 'observed_only') out.push({ text: `형제 순서: ${OBSERVED_ONLY_TEXT}`, title: '형제 묶음 사이 순서는 관측일 뿐 보장되지 않습니다.' })
  return out
})

// ---- 행 미리보기 ----
const page = shallowRef<RowPage<RowChange> | null>(null)
const loading = shallowRef(false)
const error = shallowRef<string | null>(null)
let ctrl: AbortController | null = null

watch(
  () => [store.preview?.previewId, firing.value?.id, firing.value?.rowCount, store.consumed] as const,
  async ([pid, fid, rows, consumed]) => {
    ctrl?.abort()
    page.value = null
    error.value = null
    loading.value = false
    if (!pid || !fid || !rows || consumed) return
    const mine = (ctrl = new AbortController())
    loading.value = true
    try {
      page.value = (await getRows(pid, { view: 'event', firingId: fid, offset: 0, limit: PAGE }, mine.signal)) as RowPage<RowChange>
    } catch (e) {
      if (isAbortError(e)) return
      error.value = e instanceof Error ? e.message : String(e)
    } finally {
      if (ctrl === mine) loading.value = false
    }
  },
  { immediate: true },
)

const NULL = 'NULL'
const val = (v: string | null | undefined) => (v === null ? NULL : (v ?? ''))
/** 한 행을 "열 값" 글로: UPDATE는 바뀐 열 "이전 → 이후", 삭제·추가는 키 밖의 값 */
function rowValues(r: RowChange): string {
  if (r.operation === 'UPDATE') {
    return r.changedColumns.map((c) => `${c}: ${val(r.before?.[c])} → ${val(r.after?.[c])}`).join(' · ') || '바뀐 열 없음'
  }
  const src = r.operation === 'DELETE' ? r.before : r.after
  if (!src) return '값 없음(키만 수집)'
  const keyCols = new Set(r.key.columns)
  const parts = Object.entries(src)
    .filter(([c]) => !keyCols.has(c))
    .map(([c, v]) => `${c}=${val(v)}`)
  return parts.join(' · ') || '-'
}

// ---- 포커스·닫기 ----
const heading = useTemplateRef<HTMLHeadingElement>('heading')
/** 서랍을 열기 직전에 포커스가 있던 요소(닫을 때 돌아갈 곳) */
let opener: HTMLElement | null = null
function focusHeading(): void {
  const a = document.activeElement
  if (a instanceof HTMLElement && a !== document.body && !a.closest('.gd')) opener = a
  void nextTick(() => heading.value?.focus())
}
onMounted(focusHeading)
watch(() => store.drawerFocusTick, focusHeading)
onBeforeUnmount(() => ctrl?.abort())

function close(): void {
  const id = graphNodeId.value
  const back = opener
  store.setDrawerOpen(false)
  void nextTick(() => {
    const node = id ? [...document.querySelectorAll<HTMLElement>('.vue-flow__node')].find((x) => x.dataset.id === id) : undefined
    const tab = document.getElementById(`res-tab-${store.resultTab}`)
    const target = node ?? (back?.isConnected ? back : null) ?? tab
    target?.focus()
  })
}
function showRows(): void {
  store.setResultTab('rows')
}
</script>

<template>
  <div v-if="firing" class="gd" @keydown.esc.stop="close()">
    <div class="dh">
      <h3 ref="heading" tabindex="-1">
        <span class="tbl">{{ firing.table }}</span>
        <span v-if="order !== null" class="num" :title="`화면 표시 순번 ${order}`">#{{ order }}</span>
      </h3>
      <button type="button" class="btn btn-sm" @click="close()">닫기</button>
    </div>
    <p class="op" :class="firing.rowCount === 0 ? 'zero' : `op-${firing.operation.toLowerCase()}`">{{ opText }}</p>

    <dl class="kv">
      <dt>원인</dt>
      <dd>{{ CAUSE_LABEL[firing.cause.kind] }}</dd>
      <template v-if="firing.cause.foreignKey">
        <dt>FK</dt>
        <dd>
          <code>{{ firing.cause.foreignKey.name }}</code>
          · {{ FK_ACTION_TEXT[firing.cause.foreignKey.action] }} · 일치 {{ fmtN(firing.cause.foreignKey.matchedRows) }}행
        </dd>
      </template>
      <template v-if="firing.cause.triggerCandidates.length">
        <dt>후보 트리거</dt>
        <dd><code v-for="t in firing.cause.triggerCandidates" :key="t" class="cand">{{ t }}</code></dd>
      </template>
      <dt>확신도</dt>
      <dd>
        <span class="chip" :class="firing.cause.confidence === 'certain' ? 'chip-ok' : firing.cause.confidence === 'unexplained' ? 'chip-err' : 'chip-warn'">
          {{ CONFIDENCE_LABEL[firing.cause.confidence] }}
        </span>
      </dd>
      <template v-if="flags.length">
        <dt>표시</dt>
        <dd class="flags">
          <span v-for="f in flags" :key="f.text" class="chip chip-info" :title="f.title">{{ f.text }}</span>
        </dd>
      </template>
    </dl>

    <section class="rows" aria-label="이 묶음의 행">
      <h4>행 미리보기</h4>
      <p v-if="firing.rowCount === 0" class="hint">발동했지만 바뀐 행이 없습니다.</p>
      <p v-else-if="store.consumed" class="hint">미리보기가 끝나 행을 더 읽을 수 없습니다.</p>
      <p v-else-if="loading" class="hint" role="status">행을 읽는 중…</p>
      <p v-else-if="error" class="hint err" role="alert">행을 읽지 못했습니다: {{ error }}</p>
      <template v-else-if="page">
        <p v-if="page.rows.length === 0" class="hint">받은 행이 없습니다.</p>
        <table v-else class="data">
          <thead>
            <tr>
              <th scope="col">키</th>
              <th scope="col">값</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="r in page.rows" :key="r.id">
              <td class="mono">{{ keyText(r.key) }}</td>
              <td>{{ rowValues(r) }}</td>
            </tr>
          </tbody>
        </table>
        <p v-if="page.total > page.rows.length" class="hint">전체 {{ fmtN(page.total) }}행 중 앞 {{ page.rows.length }}행</p>
      </template>
    </section>

    <div class="acts">
      <button type="button" class="btn btn-sm" @click="showRows()">행 변경 탭에서 보기</button>
    </div>
  </div>
</template>

<style scoped>
.gd {
  display: grid;
  gap: 8px;
}
.dh {
  display: flex;
  gap: 8px;
  align-items: flex-start;
  justify-content: space-between;
}
h3 {
  font: 600 14px / 1.4 var(--mono);
  color: var(--fg);
  word-break: break-all;
}
h3:focus {
  outline: none;
}
h3:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}
.num {
  margin-left: 6px;
  color: var(--muted);
  font-weight: 700;
}
.op {
  font: 700 16px var(--ui);
}
.op-delete {
  color: var(--del-fg);
}
.op-update {
  color: var(--warn);
}
.op-insert {
  color: var(--add-fg);
}
.op.zero {
  color: var(--muted);
}
.kv {
  font-size: 13px;
}
.cand {
  display: block;
}
.flags {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}
.rows h4 {
  margin: 4px 0;
  font: 600 13px var(--ui);
  color: var(--fg);
}
.rows td {
  overflow-wrap: anywhere;
}
.mono {
  font-family: var(--mono);
}
.err {
  color: var(--err);
}
.acts {
  display: flex;
  gap: 6px;
}
</style>
