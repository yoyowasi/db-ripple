<script setup lang="ts">
// D안 테이블 단위 원인 트리: 테이블당 한 줄(순변경, 열 합 = 합계), 들여쓰기는 원인 관계(문장 → 직접·FK → 트리거 → 테이블).
// 같은 행을 여러 원인이 바꾼 테이블은 [같은 행 n번]을 펼쳐 모든 경로를 본다. 관측 안 된 트리거는 제자리에 흐린 줄로 둔다.
// 같은 층 줄은 이름순(순서가 실행 순서처럼 보이지 않게). 줄을 고르면(클릭·Enter, 위아래 화살표) 아래 창이 그 테이블을 보인다.
import { computed, nextTick, ref, watch } from 'vue'
import type { PreviewResult } from '../../api/types'
import { tableTree } from '../../lib/compare'
import type { ConfLevel, TableTreeItem } from '../../lib/compare'
import { formatCount } from '../../lib/format'
import { netTotals } from '../../lib/summary'
import PathLine from './PathLine.vue'

const props = defineProps<{ preview: PreviewResult; selected: string | null }>()
const emit = defineEmits<{ select: [table: string] }>()

const tree = computed(() => tableTree(props.preview))
const totals = computed(() => netTotals(props.preview.netSummary))
const changed = computed(() => props.preview.firings.filter((f) => f.rowCount > 0).length)
const zeroAll = computed(() => props.preview.firings.filter((f) => f.rowCount === 0).length)
type TItem = Extract<TableTreeItem, { kind: 'table' }>
const tables = computed(() => tree.value.items.filter((x): x is TItem => x.kind === 'table'))

const CONF_TEXT: Record<ConfLevel, string> = { certain: '확실', inferred: '추론', mixed: '확실·추론', other: '모호' }
const n = formatCount

const open = ref(new Set<string>())
watch(
  () => props.preview.previewId,
  () => (open.value = new Set()),
)
function toggle(t: string): void {
  const s = new Set(open.value)
  if (s.has(t)) s.delete(t)
  else s.add(t)
  open.value = s
}

const body = ref<HTMLElement | null>(null)
const focusT = computed(() => {
  const ts = tables.value.map((x) => x.key)
  return props.selected && ts.includes(props.selected) ? props.selected : (ts[0] ?? null)
})
function onKey(e: KeyboardEvent, t: string): void {
  const list = tables.value
  const i = list.findIndex((x) => x.key === t)
  let next: number | null = null
  if (e.key === 'ArrowDown') next = Math.min(list.length - 1, i + 1)
  else if (e.key === 'ArrowUp') next = Math.max(0, i - 1)
  else if (e.key === 'Home') next = 0
  else if (e.key === 'End') next = list.length - 1
  else if (e.key === 'Enter' || e.key === ' ') {
    e.preventDefault()
    emit('select', t)
    return
  }
  else if (e.key === 'ArrowRight' && list[i]?.others) {
    if (!open.value.has(t)) toggle(t)
    return
  } else if (e.key === 'ArrowLeft' && open.value.has(t)) {
    toggle(t)
    return
  }
  if (next === null) return
  e.preventDefault()
  const nt = list[next].key
  emit('select', nt)
  void nextTick(() => body.value?.querySelector<HTMLElement>(`tr[data-t="${CSS.escape(nt)}"]`)?.focus())
}
/** 행 수 막대(리디자인 시안의 테이블별 막대): 가장 큰 테이블 = 100%. 색은 첫 원인(직접 빨강 · FK 파랑 · 트리거 황색) */
const maxTotal = computed(() => Math.max(1, ...tables.value.map((x) => x.row.total)))
function bar(it: TItem): { w: string; tone: string } {
  const b = it.row.basis.split(' · ')[0] ?? ''
  const tone = b.startsWith('FK') ? 'f' : b.includes('트리거') ? 't' : b === '설명 안 됨' ? 'x' : 'd'
  return { w: `${Math.max(4, (it.row.total / maxTotal.value) * 100)}%`, tone }
}
const split = (t: string) => {
  const i = t.lastIndexOf('.')
  return i < 0 ? { schema: '', name: t } : { schema: t.slice(0, i), name: t.slice(i + 1) }
}
</script>

<template>
  <div class="ct">
    <table class="grid" aria-label="테이블 단위 원인 트리. 위아래 화살표로 옮기고 Enter로 고르며, 오른쪽 화살표로 경로를 펼칩니다">
      <colgroup>
        <col />
        <col class="c-bar" />
        <col class="c-n" />
        <col class="c-n" />
        <col class="c-n" />
        <col class="c-conf" />
      </colgroup>
      <thead>
        <tr>
          <th scope="col">원인과 테이블</th>
          <th scope="col"><span class="sr-only">행 수 막대</span></th>
          <th scope="col" class="n"><span aria-hidden="true">− </span>삭제</th>
          <th scope="col" class="n"><span aria-hidden="true">≠ </span>수정</th>
          <th scope="col" class="n"><span aria-hidden="true">+ </span>추가</th>
          <th scope="col">확신도</th>
        </tr>
      </thead>
      <tbody ref="body">
        <tr class="total">
          <th scope="row">합계 · 순변경 <span class="tcount">{{ n(totals.tables) }}개 테이블</span></th>
          <td aria-hidden="true"></td>
          <td class="n" :class="totals.deleted ? 'v-del' : 'zero'">{{ totals.deleted ? n(totals.deleted) : '·' }}</td>
          <td class="n" :class="totals.updated ? 'v-chg' : 'zero'">{{ totals.updated ? n(totals.updated) : '·' }}</td>
          <td class="n" :class="totals.inserted ? 'v-add' : 'zero'">{{ totals.inserted ? n(totals.inserted) : '·' }}</td>
          <td class="tmeta">발동 {{ n(preview.firings.length) }} · 행 변경 {{ n(changed) }}</td>
        </tr>
        <tr class="legend">
          <td colspan="6">
            들여쓰기 = 원인 · <span class="lg">──</span> 확실 <span class="lg inf">┄┄</span> 추론(적용 전에 아래 창에서 값 확인) · 위아래 순서는 실행 순서가 아님
          </td>
        </tr>

        <template v-for="it in tree.items" :key="it.key">
          <tr v-if="it.kind === 'stmt'" class="stmt">
            <td colspan="6">
              <span class="chip">{{ it.title }}</span>
              <span class="mono">{{ it.text }}</span>
            </td>
          </tr>

          <tr v-else-if="it.kind === 'trigger'" class="trg" :class="{ noobs: it.observed === false }">
            <td colspan="6">
              <span class="ind" :style="{ width: `${it.level * 22}px` }" aria-hidden="true"></span>
              <span class="cn inf" aria-hidden="true">└┄</span>
              <svg class="bolt" viewBox="0 0 12 14" aria-hidden="true"><path d="M7 1L2 8h3.5L4.5 13 10 6H6.5z" /></svg>
              <span class="tg">트리거</span>
              <span class="mono nmt" :title="it.name">{{ it.name }}</span>
              <span v-if="it.observed === true" class="tag">관측됨</span>
              <span v-else-if="it.observed === false" class="tag dash" title="정적 분석상 불렸을 수 있으나 감사 로그에 흔적이 없습니다">관측 안 됨</span>
              <span class="zn">
                <template v-if="it.selfRows">이 테이블을 다시 {{ n(it.selfRows) }}행 수정(같은 행)</template>
                <template v-else-if="it.noRows">변경 없음</template>
                <template v-if="it.zero"> · 0행 쓰기 {{ n(it.zero) }}건</template>
              </span>
            </td>
          </tr>

          <template v-else>
            <tr
              class="item"
              :data-t="it.key"
              :aria-current="selected === it.key ? 'true' : undefined"
              :tabindex="it.key === focusT ? 0 : -1"
              @click="emit('select', it.key)"
              @keydown="onKey($event, it.key)"
            >
              <th scope="row" class="tn">
                <span class="ind" :style="{ width: `${it.level * 22}px` }" aria-hidden="true"></span>
                <span class="cn" :class="{ inf: it.row.conf !== 'certain' }" aria-hidden="true">{{ it.row.conf === 'certain' ? '└─' : '└┄' }}</span>
                <span v-if="it.cause" class="why" :title="it.cause.title">{{ it.cause.text }}</span>
                <span class="nm" :title="it.key"><span class="sc">{{ split(it.key).schema }}.</span>{{ split(it.key).name }}</span>
                <button
                  v-if="it.others"
                  type="button"
                  class="tag btn-tag"
                  :aria-expanded="open.has(it.key)"
                  :title="`이 테이블을 바꾼 묶음이 ${it.others + 1}개입니다. 눌러서 모든 경로 보기`"
                  @click.stop="toggle(it.key)"
                >
                  같은 행 {{ it.others + 1 }}번 {{ open.has(it.key) ? '▾' : '▸' }}
                </button>
                <span v-if="it.row.keysOnly" class="tag">키만</span>
              </th>
              <td class="barc" aria-hidden="true">
                <span v-if="it.row.total" class="trk"><i :class="bar(it).tone" :style="{ width: bar(it).w }"></i></span>
              </td>
              <td class="n" :class="it.row.deleted ? 'v-del' : 'zero'">{{ it.row.deleted ? `−${n(it.row.deleted)}` : '·' }}</td>
              <td class="n" :class="it.row.updated ? 'v-chg' : 'zero'">{{ it.row.updated ? `≠${n(it.row.updated)}` : '·' }}</td>
              <td class="n" :class="it.row.inserted ? 'v-add' : 'zero'">{{ it.row.inserted ? `+${n(it.row.inserted)}` : '·' }}</td>
              <td class="conf" :class="it.row.conf">
                <svg viewBox="0 0 26 8" aria-hidden="true"><line x1="0" y1="4" x2="26" y2="4" /></svg>
                {{ CONF_TEXT[it.row.conf] }}
              </td>
            </tr>
            <tr v-if="it.others && open.has(it.key)" class="paths">
              <td colspan="6">
                <div class="pwrap" :style="{ paddingLeft: `${it.level * 22 + 32}px` }">
                  <PathLine :preview="preview" :table="it.key" />
                </div>
              </td>
            </tr>
          </template>
        </template>

        <tr v-if="tree.zeroElsewhere || tree.triggersElsewhere.length" class="foot">
          <td colspan="6">
            그 밖에
            <template v-if="tree.zeroElsewhere">0행 쓰기 {{ n(tree.zeroElsewhere) }}건</template>
            <template v-if="tree.zeroElsewhere && tree.triggersElsewhere.length"> · </template>
            <template v-if="tree.triggersElsewhere.length">
              변경 없는 묶음이 부른 트리거 <span class="mono">{{ tree.triggersElsewhere.join(', ') }}</span>
            </template>
            <span class="muted"> (전체 0행 쓰기 {{ n(zeroAll) }}건은 [발동 묶음]·[전파 그래프]에서)</span>
          </td>
        </tr>
      </tbody>
    </table>
  </div>
</template>

<style scoped>
.ct {
  overflow: auto;
  overscroll-behavior: contain;
}
.grid {
  width: 100%;
  border-collapse: collapse;
  table-layout: fixed;
}
.c-n {
  width: 92px;
}
.c-conf {
  width: 120px;
}
.c-bar {
  width: 96px;
}
.barc .trk {
  display: block;
  border-radius: 9px;
  background: var(--line);
}
.barc i {
  display: block;
  height: 6px;
  border-radius: 9px;
  background: var(--del-mk);
  transform-origin: left;
}
.barc i.f {
  background: var(--k-fk);
}
.barc i.t {
  background: var(--k-fires);
}
.barc i.x {
  background: repeating-linear-gradient(90deg, var(--err) 0 4px, transparent 4px 7px);
}
@media (prefers-reduced-motion: no-preference) {
  .barc i {
    animation: tt-grow 0.7s 0.2s var(--ease) both;
  }
}
@keyframes tt-grow {
  from {
    transform: scaleX(0);
  }
}
.grid th,
.grid td {
  height: 36px;
  padding: 0 12px;
  border-bottom: 1px solid var(--line);
  text-align: left;
  font-weight: 400;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.grid thead th {
  position: sticky;
  top: 0;
  z-index: 1;
  height: 32px;
  background: var(--sunk);
  color: var(--muted);
  font-size: 12px;
  font-weight: 600;
}
.grid .n {
  text-align: right;
  font: 14px var(--mono);
  font-variant-numeric: tabular-nums;
}
.grid thead .n {
  font: 600 12px var(--ui);
}
.total th,
.total td {
  height: 46px;
  background: var(--sunk);
  border-bottom: 0;
  font-size: 15px;
  font-weight: 700;
}
.total .n {
  font-size: 16px;
  font-weight: 700;
}
.tcount {
  margin-left: 4px;
  color: var(--muted);
  font-weight: 500;
}
.total .tmeta {
  color: var(--muted);
  font-size: 12px;
  font-weight: 400;
}
.legend td {
  height: auto;
  padding: 0 12px 6px;
  border-bottom: 2px solid var(--fg);
  background: var(--sunk);
  color: var(--muted);
  font-size: 12px;
  white-space: normal;
}
.lg {
  font: 700 12px var(--mono);
  color: var(--fg);
}
.lg.inf {
  color: var(--muted);
  font-weight: 400;
}
.stmt td {
  height: 34px;
  font-size: 13px;
}
.chip {
  display: inline-block;
  margin-right: 8px;
  padding: 0 9px;
  border-radius: 99px;
  background: var(--fg);
  color: var(--panel);
  font: 600 12px / 1.6 var(--ui);
}
.ind {
  display: inline-block;
}
.cn {
  display: inline-block;
  width: 24px;
  color: var(--fg);
  font: 700 13px var(--mono);
}
.cn.inf {
  color: var(--muted);
  font-weight: 400;
}
.why {
  margin-right: 8px;
  color: var(--muted);
  font-size: 12px;
}
.nm {
  font-weight: 600;
}
.nm .sc {
  color: var(--muted);
  font-weight: 400;
}
.tag {
  display: inline-block;
  margin-left: 8px;
  padding: 0 6px;
  border: 1px solid var(--ctl);
  border-radius: 99px;
  background: none;
  color: var(--muted);
  font: 500 11px / 1.6 var(--ui);
  vertical-align: 1px;
}
.tag.dash {
  border-style: dashed;
}
.btn-tag {
  cursor: pointer;
}
.btn-tag:hover,
.btn-tag[aria-expanded='true'] {
  border-color: var(--accent);
  color: var(--fg);
}
.trg td {
  height: 30px;
  font-size: 12px;
  color: var(--muted);
}
.trg .nmt {
  color: var(--fg);
}
.trg.noobs .nmt {
  color: var(--muted);
  font-style: italic;
}
.bolt {
  width: 10px;
  height: 12px;
  margin-right: 4px;
  fill: var(--warn);
  vertical-align: -1px;
}
.tg {
  margin-right: 6px;
}
.zn {
  margin-left: 10px;
}
.item {
  cursor: pointer;
}
.item:hover > * {
  background: var(--sunk);
}
.item[aria-current='true'] > * {
  background: color-mix(in srgb, var(--accent) 9%, var(--panel));
}
.item[aria-current='true'] > th {
  box-shadow: inset 4px 0 var(--accent);
}
.item:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}
.v-del {
  color: var(--del-fg);
  font-weight: 700;
}
.v-chg {
  color: var(--warn);
  font-weight: 700;
}
.v-add {
  color: var(--add-fg);
  font-weight: 700;
}
.zero {
  color: var(--muted);
}
.conf {
  font-size: 13px;
}
.conf svg {
  width: 26px;
  height: 8px;
  margin-right: 6px;
  vertical-align: middle;
  overflow: visible;
}
.conf line {
  stroke: var(--fg);
  stroke-width: 2;
}
.conf.inferred,
.conf.mixed,
.conf.other {
  color: var(--muted);
}
.conf.inferred line,
.conf.other line {
  stroke: var(--muted);
  stroke-dasharray: 4 3;
}
.conf.mixed line {
  stroke: var(--muted);
  stroke-dasharray: 10 3 3 3;
}
.paths td {
  height: auto;
  padding: 6px 12px 8px;
  background: var(--sunk);
  white-space: normal;
}
.foot td {
  height: auto;
  padding: 8px 12px;
  color: var(--muted);
  font-size: 12px;
  white-space: normal;
}
</style>
