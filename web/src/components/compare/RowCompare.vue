<script setup lang="ts">
// 좌우 행 비교(시안 3-compare): 고른 테이블의 최종 차이(net) 행을 "지금(원본)" | "적용 후"로 맞대 보인다.
// 행은 서버 페이지로 읽는다(rows?view=net&table=). 수정 행은 키·바뀐 열만, 추가·삭제 행은 앞 12열만 먼저 보이고 [모두 보기]로 편다.
import { computed, ref, toRef, watch } from 'vue'
import type { SchemaTable } from '../../api/types'
import { useRowPage } from '../../composables/useRowPage'
import { VOLATILE_LABEL, formatCount } from '../../lib/format'
import { columnMeta, netRowToGrid } from '../../lib/gridRows'
import type { GridRow } from '../../lib/gridRows'
import type { NetChange } from '../../api/types'
import ProblemBanner from '../ProblemBanner.vue'
import RowPager from '../rows/RowPager.vue'

const props = defineProps<{
  previewId: string
  table: string
  /** 서버에서 미리보기가 지워졌다(적용·취소): 더 읽지 않는다 */
  consumed: boolean
  schemaTable: SchemaTable | undefined
  originDb: string
}>()

const view = ref<'net'>('net')
const firing = ref<string | null>(null)
const enabled = computed(() => !props.consumed)
const { page, loading, error, offset, limit, next, prev } = useRowPage(
  toRef(props, 'previewId'),
  view,
  firing,
  toRef(props, 'table'),
  enabled,
)

const meta = computed(() => columnMeta(props.schemaTable))
const rows = computed<GridRow[]>(() => ((page.value?.rows ?? []) as NetChange[]).map((r, i) => netRowToGrid(r, offset.value + i, meta.value)))

const OP: Record<GridRow['op'], { cls: string; text: string; left: boolean; right: boolean }> = {
  DELETE: { cls: 'del', text: '삭제', left: true, right: false },
  MULTISET_REMOVED: { cls: 'del', text: '제거', left: true, right: false },
  INSERT: { cls: 'add', text: '추가', left: false, right: true },
  MULTISET_ADDED: { cls: 'add', text: '추가', left: false, right: true },
  UPDATE: { cls: 'mod', text: '수정', left: true, right: true },
}
const FIRST = 12

/** 열 순서: 스키마 열 순서, 그다음 행에만 있는 열. 키 열을 맨 앞에. */
function columnsOf(r: GridRow): string[] {
  const keys = Object.keys(r.keyValues ?? {})
  const present = new Set([...keys, ...Object.keys(r.before ?? {}), ...Object.keys(r.after ?? {})])
  const ordered = (props.schemaTable?.columns.map((c) => c.name) ?? []).filter((c) => present.has(c))
  const rest = [...present].filter((c) => !ordered.includes(c))
  const all = [...ordered, ...rest]
  return [...keys, ...all.filter((c) => !keys.includes(c))]
}

const expanded = ref(new Set<string>())
watch(
  () => [props.previewId, props.table],
  () => (expanded.value = new Set()),
)
function toggle(id: string): void {
  const s = new Set(expanded.value)
  if (s.has(id)) s.delete(id)
  else s.add(id)
  expanded.value = s
}

interface Line {
  col: string
  key: boolean
  changed: boolean
}
function linesOf(r: GridRow): { shown: Line[]; hidden: number } {
  const keys = new Set(Object.keys(r.keyValues ?? {}))
  const all = columnsOf(r).map((col) => ({ col, key: keys.has(col), changed: r.changed.has(col) }))
  if (expanded.value.has(r.rowId)) return { shown: all, hidden: 0 }
  const want = r.op === 'UPDATE' ? all.filter((l) => l.key || l.changed) : all.slice(0, FIRST)
  return { shown: want, hidden: all.length - want.length }
}

/** 한쪽 이미지의 한 칸. 이미지에 열이 있으면 그 값(NULL 포함), 없으면 키 값, 그것도 없으면 "수집 안 됨"(실제 NULL과 구분). */
function cell(img: GridRow['before'], r: GridRow, col: string): { text: string; nul: boolean; none: boolean } {
  if (img && Object.prototype.hasOwnProperty.call(img, col)) {
    const v = img[col]
    return { text: v === null ? 'NULL' : v, nul: v === null, none: false }
  }
  const k = r.keyValues?.[col]
  if (k !== undefined) return { text: k === null ? 'NULL' : k, nul: k === null, none: false }
  return { text: '수집 안 됨', nul: false, none: true }
}

const pageText = computed(() => {
  const p = page.value
  if (!p) return ''
  const end = p.offset + p.rows.length
  return `${formatCount(p.offset + (p.rows.length ? 1 : 0))}–${formatCount(end)} / ${formatCount(p.total)}행`
})
const canPrev = computed(() => offset.value > 0)
const canNext = computed(() => !!page.value && offset.value + limit.value < page.value.total)
</script>

<template>
  <div class="rc" :aria-busy="loading">
    <ProblemBanner v-if="error" :error="error" title="행을 읽지 못했습니다." />
    <p v-else-if="consumed && !page" class="hint pad">이 미리보기는 서버에서 지워져 행을 더 읽을 수 없습니다.</p>
    <p v-else-if="!page && loading" class="hint pad">행을 읽는 중…</p>
    <template v-else-if="page">
      <table class="cmp">
        <caption class="sr-only">{{ table }} 행 비교: 지금(원본) | 적용 후</caption>
        <colgroup>
          <col class="c-mk" />
          <col class="c-nm" />
          <col />
          <col />
        </colgroup>
        <thead>
          <tr>
            <th scope="col"><span class="sr-only">바뀜</span></th>
            <th scope="col">열</th>
            <th scope="col">지금 <small>원본 {{ originDb }}</small></th>
            <th scope="col" class="r">적용 후 <small>미리보기 결과</small></th>
          </tr>
        </thead>
        <tbody v-for="r in rows" :key="r.rowId">
          <tr class="rec">
            <td colspan="4">
              <span class="op" :class="OP[r.op].cls">{{ OP[r.op].text }}</span>
              <span class="mono">{{ r.keyText }}</span>
              <span v-if="r.multiplicity && r.multiplicity > 1" class="muted"> × {{ r.multiplicity }}</span>
              <span v-if="!r.before && !r.after" class="tag">키만 수집</span>
              <span v-if="r.note" class="muted"> · {{ r.note }}</span>
            </td>
          </tr>
          <tr v-for="l in linesOf(r).shown" :key="l.col">
            <td class="mk" :aria-label="l.changed ? '바뀜' : undefined">{{ l.changed ? '≠' : '' }}</td>
            <th scope="row" class="nm">
              <span class="mono">{{ l.col }}</span>
              <span v-if="l.key" class="tag">키</span>
              <span v-if="r.derived.has(l.col)" class="tag" title="계산 열이라 식으로 정해집니다">파생</span>
              <span
                v-if="r.volatile.get(l.col)"
                class="tag vol"
                :title="`${VOLATILE_LABEL[r.volatile.get(l.col)!]}. 원본에 적용하면 값이 달라질 수 있습니다`"
              >예상값</span>
            </th>
            <td v-if="!OP[r.op].left" class="v absent">없음</td>
            <td
              v-else
              class="v"
              :class="{
                del: r.op === 'DELETE' || r.op === 'MULTISET_REMOVED',
                chg: l.changed,
                nul: cell(r.before, r, l.col).nul,
                none: cell(r.before, r, l.col).none,
              }"
            >
              {{ cell(r.before, r, l.col).text }}
            </td>
            <td v-if="!OP[r.op].right" class="v r absent">없음</td>
            <td
              v-else
              class="v r"
              :class="{
                add: r.op === 'INSERT' || r.op === 'MULTISET_ADDED',
                chg: l.changed,
                nul: cell(r.after, r, l.col).nul,
                none: cell(r.after, r, l.col).none,
              }"
            >
              {{ cell(r.after, r, l.col).text }}
            </td>
          </tr>
          <tr v-if="linesOf(r).hidden || expanded.has(r.rowId)" class="more">
            <td></td>
            <td colspan="3">
              <template v-if="linesOf(r).hidden">
                그 밖의 열 {{ linesOf(r).hidden }}개{{ r.op === 'UPDATE' ? '는 같음' : '' }} ·
              </template>
              <button type="button" class="linkbtn" @click="toggle(r.rowId)">{{ expanded.has(r.rowId) ? '접기' : '모두 보기' }}</button>
            </td>
          </tr>
        </tbody>
      </table>
      <p v-if="rows.length === 0" class="hint pad">이 테이블에 바뀐 행이 없습니다.</p>
      <div v-if="page.total > rows.length" class="pg">
        <RowPager :text="pageText" :loading="loading" :can-prev="canPrev" :can-next="canNext" @prev="prev()" @next="next()" />
      </div>
    </template>
  </div>
</template>

<style scoped>
.rc {
  min-width: 0;
}
.pad {
  padding: 14px;
}
.pg {
  padding: 0 14px;
}
.cmp {
  width: 100%;
  border-collapse: separate;
  border-spacing: 0;
  table-layout: fixed;
  font-size: 13px;
}
.c-mk {
  width: 30px;
}
.c-nm {
  width: 240px;
}
.cmp thead th {
  position: sticky;
  top: 0;
  z-index: 1;
  padding: 7px 12px;
  border-bottom: 1px solid var(--ctl);
  background: var(--sunk);
  text-align: left;
  font-size: 12px;
}
.cmp thead th small {
  margin-left: 6px;
  color: var(--muted);
  font-weight: 400;
}
.cmp td,
.cmp th[scope='row'] {
  padding: 4px 12px;
  border-bottom: 1px solid var(--line);
  vertical-align: top;
  overflow-wrap: anywhere;
  text-align: left;
  font-weight: 400;
}
.cmp .r {
  border-left: 2px solid var(--ctl);
}
.mk {
  padding: 4px 0 4px 12px !important;
  text-align: center;
  color: var(--warn);
  font-weight: 800;
}
.nm {
  color: var(--muted);
}
.v {
  font: 13px var(--mono);
}
.v.nul {
  color: var(--muted);
  font-style: italic;
}
.v.none {
  color: var(--muted);
  font: 12px var(--ui);
}
.v.absent {
  background: repeating-linear-gradient(135deg, var(--hatch) 0 6px, transparent 6px 12px);
  color: var(--muted);
  font: 12px var(--ui);
}
.v.del {
  background: var(--del-bg);
  color: var(--del-fg);
}
.v.add {
  background: var(--add-bg);
  color: var(--add-fg);
}
.v.chg {
  background: var(--chg-bg);
  color: var(--fg);
  font-weight: 700;
}
.rec td {
  padding: 6px 12px;
  border-bottom: 1px solid var(--ctl);
  background: var(--sunk);
  font-weight: 600;
}
.op {
  display: inline-block;
  min-width: 40px;
  margin-right: 8px;
  padding: 0 6px;
  border-radius: 3px;
  color: #fff;
  font-size: 12px;
  text-align: center;
}
:root[data-theme='dark'] .op {
  color: #0a1020;
}
.op.del {
  background: var(--del-mk);
}
.op.add {
  background: var(--add-mk);
}
.op.mod {
  background: var(--chg-mk);
}
.tag {
  display: inline-block;
  margin-left: 6px;
  padding: 0 5px;
  border: 1px solid var(--ctl);
  border-radius: 3px;
  color: var(--muted);
  font: 500 11px / 1.5 var(--ui);
}
.tag.vol {
  border-color: transparent;
  background: var(--warn-bg);
  color: var(--warn);
}
.more td {
  padding: 4px 12px;
  color: var(--muted);
  font-size: 12px;
}
.linkbtn {
  padding: 0;
  border: 0;
  background: none;
  color: var(--accent);
  font: inherit;
  text-decoration: underline;
  text-underline-offset: 2px;
  cursor: pointer;
}
</style>
