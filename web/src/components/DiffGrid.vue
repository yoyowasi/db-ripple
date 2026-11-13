<script setup lang="ts">
// "행 변경" 탭(C3). 발동 보기 = 묶음 목록 → 고른 묶음의 행, 최종 차이 보기 = 테이블 선택 → 그 테이블의 행.
// 행은 서버 페이지로 읽는다(useRowPage). 서버 rows API에는 테이블 필터가 없어 최종 차이의 테이블 거르기는 읽은 페이지 안에서만 한다.
import { computed, ref } from 'vue'
import type { NetChange, PreviewResult, RowChange, RowView } from '../api/types'
import { useRowPage } from '../composables/useRowPage'
import { OP_LABEL, formatCount } from '../lib/format'
import type { GridRow } from '../lib/gridRows'
import { columnMeta, eventRowToGrid, netRowToGrid } from '../lib/gridRows'
import { CAUSE_LABEL, tableBadges } from '../lib/summary'
import { usePreviewStore } from '../stores/preview'
import { useSandboxStore } from '../stores/sandbox'
import FiringTable from './FiringTable.vue'
import ProblemBanner from './ProblemBanner.vue'
import RowGrid from './rows/RowGrid.vue'
import RowPager from './rows/RowPager.vue'

const props = defineProps<{
  preview: PreviewResult | null
  selectedFiringId: string | null
  view: RowView
  showZeroRows: boolean
  /** 서버에서 이미 지워졌다(적용·취소). 마지막으로 읽은 페이지만 보인다. */
  consumed: boolean
}>()

const emit = defineEmits<{
  'update:selectedFiringId': [id: string | null]
  'update:view': [view: RowView]
}>()

const sandbox = useSandboxStore()
const store = usePreviewStore()

// ---- 보기 전환(탭과 같은 밑줄 방식, roving tabindex) ----
const VIEWS: { id: RowView; label: string }[] = [
  { id: 'event', label: '발동 보기' },
  { id: 'net', label: '최종 차이 보기' },
]
const tabId = (v: RowView) => `rv-tab-${v}`

function onViewKey(e: KeyboardEvent): void {
  const i = VIEWS.findIndex((v) => v.id === props.view)
  let next: number | null = null
  if (e.key === 'ArrowRight') next = (i + 1) % VIEWS.length
  else if (e.key === 'ArrowLeft') next = (i + VIEWS.length - 1) % VIEWS.length
  else if (e.key === 'Home') next = 0
  else if (e.key === 'End') next = VIEWS.length - 1
  if (next === null) return
  e.preventDefault()
  setView(VIEWS[next].id)
  document.getElementById(tabId(VIEWS[next].id))?.focus()
}
function setView(v: RowView): void {
  if (!props.consumed && v !== props.view) emit('update:view', v)
}

// ---- 읽기 ----
const firings = computed(() => props.preview?.firings ?? [])
const selFiring = computed(() => firings.value.find((f) => f.id === props.selectedFiringId) ?? null)

const previewId = computed(() => props.preview?.previewId ?? null)
const viewRef = computed(() => props.view)
// 최종 차이 보기는 묶음으로 거르지 않고 전체를 읽는다(테이블은 아래에서 거른다).
const firingRef = computed(() => (props.view === 'event' ? props.selectedFiringId : null))
// 발동 보기는 묶음을 골라야 읽고, 0행 묶음은 읽을 것이 없다.
const enabled = computed(
  () => !!props.preview && !props.consumed && (props.view === 'net' || (!!selFiring.value && selFiring.value.rowCount > 0)),
)

const { page, loading, error, offset, limit, next, prev } = useRowPage(previewId, viewRef, firingRef, enabled)

const tableOf = computed(() => new Map(firings.value.map((f) => [f.id, f.table])))

/** 읽은 행. 다른 보기·다른 묶음에서 읽다 만 페이지가 남아 있으면 보이지 않게 한다. */
const pageRows = computed<GridRow[]>(() => {
  const p = page.value
  if (!p || p.view !== props.view) return []
  if (p.view === 'net') {
    return (p.rows as NetChange[]).map((r, i) => netRowToGrid(r, p.offset + i, columnMeta(sandbox.findTable(r.table))))
  }
  const rows = p.rows as RowChange[]
  if (rows.some((r) => r.firingId !== props.selectedFiringId)) return []
  return rows.map((r) => {
    const t = tableOf.value.get(r.firingId) ?? ''
    return eventRowToGrid(r, t, columnMeta(sandbox.findTable(t)))
  })
})

const pageLabel = computed(() => {
  const p = page.value
  if (!p || p.total === 0) return '0행'
  const to = Math.min(p.offset + p.rows.length, p.total)
  return `${formatCount(p.offset + 1)}–${formatCount(to)} / ${formatCount(p.total)}행`
})
const canPrev = computed(() => !props.consumed && offset.value > 0)
const canNext = computed(() => !props.consumed && !!page.value && offset.value + limit.value < page.value.total)

// ---- 발동 보기 ----
const zeroCount = computed(() => firings.value.filter((f) => f.rowCount === 0).length)
const visibleFirings = computed(() =>
  firings.value.filter((f) => props.showZeroRows || f.rowCount > 0 || f.id === props.selectedFiringId),
)
function toggleZero(): void {
  store.showZeroRows = !props.showZeroRows
}

const selOrder = computed(() => (selFiring.value ? store.firingOrder.get(selFiring.value.id) : undefined))
const selCause = computed(() => {
  const c = selFiring.value?.cause
  if (!c) return null
  return {
    kind: CAUSE_LABEL[c.kind],
    name: c.foreignKey?.name ?? c.triggerCandidates.join(', '),
  }
})
const selKeysOnly = computed(() => selFiring.value?.imageMode === 'KEYS')
const hasUnpaired = computed(() => pageRows.value.some((r) => r.unpaired))

// ---- 최종 차이 보기 ----
const netOptions = computed(() => {
  const p = props.preview
  if (!p) return []
  const badges = tableBadges(p)
  return p.netSummary
    .filter((s) => s.inserted + s.updated + s.deleted + s.multisetAdded + s.multisetRemoved > 0)
    .map((s) => ({ table: s.table, label: `${s.table} · ${badges.get(s.table)?.text ?? '변경'}` }))
})
const pickedTable = ref<string | null>(null)
/** 고른 테이블. 안 골랐거나 목록에 없으면 고른 묶음의 테이블, 그것도 없으면 첫 테이블. */
const netTable = computed(() => {
  const names = netOptions.value.map((o) => o.table)
  if (pickedTable.value && names.includes(pickedTable.value)) return pickedTable.value
  const fromFiring = selFiring.value?.table
  if (fromFiring && names.includes(fromFiring)) return fromFiring
  return names[0] ?? null
})
const netRows = computed(() => pageRows.value.filter((r) => r.table === netTable.value))
// 읽은 페이지가 전체가 아니면 거른 결과도 일부다.
const netPartial = computed(() => !!page.value && (page.value.offset > 0 || page.value.total > page.value.rows.length))
const netLabel = computed(() => {
  if (netPartial.value) return pageLabel.value
  return `${formatCount(netRows.value.length)}행`
})
const netKeysOnly = computed(() => firings.value.some((f) => f.table === netTable.value && f.imageMode === 'KEYS'))

// E6: 키 없는 테이블의 다중집합 문구. 최종 차이 보기는 고른 테이블, 발동 보기는 고른 묶음의 테이블만.
const multisetNotes = computed(() => {
  const p = props.preview
  if (!p) return []
  const t = props.view === 'net' ? netTable.value : selFiring.value?.table
  return p.netSummary
    .filter((s) => (s.multisetRemoved > 0 || s.multisetAdded > 0) && s.table === t)
    .map((s) => ({
      table: s.table,
      text: `제거된 행 ${formatCount(s.multisetRemoved)}개, 추가된 행 ${formatCount(s.multisetAdded)}개`,
    }))
})
</script>

<template>
  <section class="rv" aria-label="행 변경">
    <div class="top">
      <div class="seg" role="tablist" aria-label="행 변경 보기 방식" @keydown="onViewKey">
        <button
          v-for="v in VIEWS"
          :id="tabId(v.id)"
          :key="v.id"
          type="button"
          role="tab"
          :aria-selected="view === v.id"
          :aria-disabled="consumed || undefined"
          aria-controls="rv-panel"
          :tabindex="view === v.id ? 0 : -1"
          @click="setView(v.id)"
        >
          {{ v.label }}
        </button>
      </div>
      <p class="hint desc">발동 보기는 묶음마다 일어난 그대로, 최종 차이 보기는 끝난 뒤 달라진 것만.</p>
    </div>

    <div v-if="consumed" class="banner banner-warn" role="status">
      <div>이 미리보기는 서버에서 지워져 마지막으로 읽은 페이지만 보입니다.</div>
    </div>
    <ProblemBanner v-if="error" :error="error" title="행을 읽지 못했습니다." />

    <div v-if="!preview" class="empty">미리보기를 실행하면 변경 행이 여기에 나옵니다.</div>

    <div v-else id="rv-panel" role="tabpanel" :aria-labelledby="tabId(view)">
      <!-- 발동 보기 -->
      <template v-if="view === 'event'">
        <div v-if="!firings.length" class="empty">발동한 묶음이 없습니다.</div>
        <template v-else>
          <FiringTable
            :firings="visibleFirings"
            :order="store.firingOrder"
            :selected-id="selectedFiringId"
            :disabled="consumed"
            @select="(id) => emit('update:selectedFiringId', id)"
          />
          <p v-if="zeroCount" class="zsum">
            0행 묶음 {{ zeroCount }}개 {{ showZeroRows ? '표시 중' : '숨김' }} ·
            <button type="button" class="lnk" @click="toggleZero">{{ showZeroRows ? '접기' : '보기' }}</button>
          </p>
          <p class="hint">번호 = 화면 표시 순번(그래프 노드와 같습니다). 관측 순서이며 실행 순서를 보장하지 않습니다.</p>

          <div class="detail">
            <template v-if="selFiring">
              <h3 :id="`rv-h-${selFiring.id}`" aria-live="polite">
                #{{ selOrder }} · {{ selFiring.table }} {{ OP_LABEL[selFiring.operation] }} {{ formatCount(selFiring.rowCount) }}행
              </h3>
              <p v-if="selCause" class="hint cause">
                <span class="chip">{{ selCause.kind }}</span>
                <code v-if="selCause.name">{{ selCause.name }}</code>
              </p>
              <p v-if="selFiring.rowCount === 0" class="hint">
                {{ OP_LABEL[selFiring.operation] }} 0행. 발동했지만 바뀐 행이 없어 표시할 행이 없습니다.
              </p>
              <template v-else>
                <p v-if="selKeysOnly" class="hint">행 이미지 상한을 넘어 키 열만 수집했습니다. 키 열의 값만 보입니다.</p>
                <p v-if="hasUnpaired" class="hint">키가 없는 테이블이라 UPDATE의 전후 짝을 확정하지 못한 행이 있습니다(삭제와 추가의 쌍으로 봅니다).</p>
                <RowGrid :rows="pageRows" :loading="loading" :label="`${selFiring.table} ${OP_LABEL[selFiring.operation]}`" />
                <RowPager :text="pageLabel" :loading="loading" :can-prev="canPrev" :can-next="canNext" @prev="prev()" @next="next()" />
              </template>
            </template>
            <p v-else class="hint">목록에서 묶음을 고르면 그 묶음의 행이 보입니다.</p>
          </div>
        </template>
      </template>

      <!-- 최종 차이 보기 -->
      <template v-else>
        <div v-if="!netOptions.length" class="empty">끝난 뒤 달라진 행이 없습니다.</div>
        <template v-else>
          <div class="selrow">
            <label for="rv-net-table">테이블</label>
            <select id="rv-net-table" :value="netTable ?? ''" :disabled="consumed" @change="pickedTable = ($event.target as HTMLSelectElement).value">
              <option v-for="o in netOptions" :key="o.table" :value="o.table">{{ o.label }}</option>
            </select>
          </div>
          <div class="detail">
            <h3>{{ netTable }}</h3>
            <p v-if="netKeysOnly" class="hint">행 이미지 상한을 넘어 키 열만 보입니다.</p>
            <div v-for="n in multisetNotes" :key="n.table" class="banner banner-info">
              <div>
                <strong>{{ n.text }}</strong>
                키가 없는 테이블이라 행 개수 차이로만 비교합니다. UPDATE의 전후 짝은 확정하지 않습니다.
              </div>
            </div>
            <p v-if="netPartial" class="hint partial" role="status">
              이 페이지 안에서만 걸렀습니다(전체 {{ formatCount(page?.total ?? 0) }}행). 다른 행은 [이전]/[다음]으로 넘겨 보세요.
            </p>
            <RowGrid :rows="netRows" :loading="loading" :label="netTable ?? ''" :linked-firing-id="selectedFiringId" />
            <RowPager :text="netLabel" :loading="loading" :can-prev="canPrev" :can-next="canNext" @prev="prev()" @next="next()" />
            <p v-if="selectedFiringId" class="hint">그래프·목록에서 고른 묶음이 바꾼 행은 왼쪽 띠로 표시됩니다.</p>
          </div>
        </template>
      </template>

      <p class="hint legend">파생 = 계산 열이라 직접 바뀐 값이 아닙니다. 예상값 = 실행 시각·IDENTITY처럼 적용 때 달라질 수 있는 값입니다.</p>
    </div>
  </section>
</template>

<style scoped>
.rv {
  display: grid;
  gap: 8px;
  align-content: start;
}
.top {
  display: flex;
  align-items: flex-end;
  flex-wrap: wrap;
  gap: 4px 16px;
  border-bottom: 1px solid var(--line);
}
.desc {
  margin: 0 0 6px auto;
}
.seg {
  display: flex;
  gap: 16px;
}
.seg button {
  position: relative;
  padding: 6px 0;
  border: 0;
  background: transparent;
  color: var(--muted);
  font: 500 13px var(--ui);
  cursor: pointer;
}
.seg button::after {
  content: '';
  position: absolute;
  left: 0;
  right: 0;
  bottom: -1px;
  height: 2px;
  background: color-mix(in srgb, var(--accent) 50%, transparent);
  transform: scaleX(0);
  transform-origin: right;
}
.seg button:hover {
  color: var(--fg);
}
.seg button:hover::after {
  transform: scaleX(1);
  transform-origin: left;
}
.seg button[aria-selected='true'] {
  color: var(--fg);
  font-weight: 600;
}
.seg button[aria-selected='true']::after {
  transform: scaleX(1);
  background: var(--accent);
}
.seg button[aria-disabled='true'] {
  cursor: not-allowed;
  opacity: 0.6;
}
.seg button:focus-visible {
  outline-offset: -2px;
}
.empty {
  padding: 28px 0;
  text-align: center;
  color: var(--muted);
}
#rv-panel {
  display: grid;
  gap: 6px;
  min-width: 0;
}
#rv-panel > p {
  margin: 0;
}
.zsum {
  font-size: 12px;
  color: var(--muted);
}
.lnk {
  border: 0;
  background: none;
  padding: 0;
  color: var(--accent);
  font: inherit;
  text-decoration: underline;
  text-underline-offset: 3px;
  cursor: pointer;
}
.detail {
  min-width: 0;
  border-top: 1px solid var(--line);
  padding-top: 6px;
  display: grid;
  gap: 6px;
}
.detail > p {
  margin: 0;
}
.detail h3 {
  margin: 4px 0 0;
  font: 700 13px var(--ui);
  overflow-wrap: anywhere;
}
.cause {
  display: flex;
  align-items: center;
  gap: 6px;
}
.selrow {
  display: flex;
  align-items: center;
  gap: 8px;
}
.selrow select {
  max-width: 100%;
}
.legend {
  margin-top: 2px;
}
</style>
