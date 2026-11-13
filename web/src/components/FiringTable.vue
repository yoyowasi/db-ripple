<script setup lang="ts">
// 발동 묶음 목록(행 변경 탭 · 발동 보기). 행을 고르면(클릭·Enter·Space) 그 묶음의 행이 아래에 보인다.
// 표시 순번(#)은 그래프 노드 번호와 같은 값(lib/summary displayOrder)이다.
import { computed, ref } from 'vue'
import type { Confidence, Firing } from '../api/types'
import { CONFIDENCE_LABEL, OP_LABEL, formatCount } from '../lib/format'
import { CAUSE_LABEL } from '../lib/summary'

const props = defineProps<{
  /** 보일 묶음(걸러진 목록) */
  firings: Firing[]
  /** 묶음 id → 표시 순번 */
  order: Map<string, number>
  selectedId: string | null
  disabled?: boolean
}>()

const emit = defineEmits<{ select: [id: string] }>()

const CONF_TEXT: Record<Confidence, string> = {
  certain: '확실',
  inferred: '추론',
  ambiguous: '모호',
  unexplained: '설명 안 됨',
}

const rows = computed(() =>
  [...props.firings]
    .sort((a, b) => (props.order.get(a.id) ?? 1e9) - (props.order.get(b.id) ?? 1e9))
    .map((f) => {
      const c = f.cause
      const name =
        c.foreignKey?.name ??
        (c.triggerCandidates.length > 1
          ? `${c.triggerCandidates[0]} 외 ${c.triggerCandidates.length - 1}`
          : (c.triggerCandidates[0] ?? ''))
      const dot = f.table.indexOf('.')
      return {
        f,
        n: props.order.get(f.id),
        schema: dot < 0 ? '' : f.table.slice(0, dot + 1),
        bare: dot < 0 ? f.table : f.table.slice(dot + 1),
        cause: CAUSE_LABEL[c.kind],
        name,
        nameFull: c.foreignKey?.name ?? c.triggerCandidates.join(', '),
        keys: f.imageMode === 'KEYS',
        auditUncertain: f.auditOrder === 'NONE',
        zero: f.rowCount === 0,
      }
    }),
)

// 키보드: 행 하나만 탭 순서에 두고(roving tabindex) 방향키로 옮긴다. Enter·Space가 고른다.
const tbody = ref<HTMLElement | null>(null)
const focusId = ref<string | null>(null)
const tabStop = computed(() => {
  const ids = rows.value.map((r) => r.f.id)
  if (focusId.value && ids.includes(focusId.value)) return focusId.value
  if (props.selectedId && ids.includes(props.selectedId)) return props.selectedId
  return ids[0] ?? null
})

function focusRow(i: number): void {
  const list = rows.value
  if (!list.length) return
  const id = list[Math.max(0, Math.min(list.length - 1, i))].f.id
  focusId.value = id
  tbody.value?.querySelector<HTMLElement>(`[data-fid="${CSS.escape(id)}"]`)?.focus()
}

function onKey(e: KeyboardEvent, i: number): void {
  if (e.key === 'ArrowDown') focusRow(i + 1)
  else if (e.key === 'ArrowUp') focusRow(i - 1)
  else if (e.key === 'Home') focusRow(0)
  else if (e.key === 'End') focusRow(rows.value.length - 1)
  else if (e.key === 'Enter' || e.key === ' ') pick(rows.value[i].f.id)
  else return
  e.preventDefault()
}

function pick(id: string): void {
  if (!props.disabled) emit('select', id)
}
</script>

<template>
  <div class="fw" role="region" tabindex="-1" aria-label="발동 묶음 목록(세로로 스크롤)">
    <table class="ft" role="grid" aria-label="발동 묶음 목록" :aria-disabled="disabled || undefined">
      <caption class="sr-only">
        발동 묶음 목록. 위아래 방향키로 옮기고 Enter로 고릅니다.
      </caption>
      <thead>
        <tr>
          <th scope="col" class="c-n">#</th>
          <th scope="col" class="c-t">테이블</th>
          <th scope="col">연산</th>
          <th scope="col" class="c-r">행 수</th>
          <th scope="col" class="c-c">원인</th>
          <th scope="col">확신도</th>
          <th scope="col">표시</th>
        </tr>
      </thead>
      <tbody ref="tbody">
        <tr
          v-for="(r, i) in rows"
          :key="r.f.id"
          role="row"
          class="fr"
          :class="{ on: r.f.id === selectedId, zero: r.zero }"
          :data-fid="r.f.id"
          :aria-selected="r.f.id === selectedId"
          :tabindex="r.f.id === tabStop ? 0 : -1"
          @click="pick(r.f.id)"
          @keydown="onKey($event, i)"
          @focus="focusId = r.f.id"
        >
          <td class="c-n">
            <span aria-hidden="true">#{{ r.n }}</span>
            <span class="sr-only">묶음 {{ r.n }}번</span>
          </td>
          <td class="c-t" :title="r.f.table">
            <span class="sch">{{ r.schema }}</span
            >{{ r.bare }}
          </td>
          <td>{{ OP_LABEL[r.f.operation] }}</td>
          <td class="c-r">{{ formatCount(r.f.rowCount) }}</td>
          <td class="c-c" :title="r.nameFull ? `${r.cause}: ${r.nameFull}` : r.cause">
            {{ r.cause }}<code v-if="r.name" class="nm"> {{ r.name }}</code>
          </td>
          <td>
            <span class="conf" :class="`conf-${r.f.cause.confidence}`" :title="CONFIDENCE_LABEL[r.f.cause.confidence]">{{
              CONF_TEXT[r.f.cause.confidence]
            }}</span>
          </td>
          <td><div class="flags">
            <span v-if="r.keys" class="chip chip-info" title="행 이미지 상한을 넘어 키 열만 수집했습니다.">키만</span>
            <span
              v-if="r.auditUncertain"
              class="chip chip-warn"
              title="이 테이블의 감사 트리거가 마지막에 실행된다는 보장이 없어 발동 순서를 확정하지 못했습니다."
              >감사 순서 불확실</span
            >
            <span v-if="r.zero" class="chip" title="발동했지만 바뀐 행이 없습니다.">0행</span>
          </div></td>
        </tr>
        <tr v-if="!rows.length">
          <td colspan="7" class="none">보일 묶음이 없습니다.</td>
        </tr>
      </tbody>
    </table>
  </div>
</template>

<style scoped>
.fw {
  max-height: 232px;
  overflow: auto;
  border: 1px solid var(--line);
  border-radius: var(--r);
  background: var(--panel);
}
.ft {
  width: 100%;
  border-collapse: collapse;
  font-size: 13px;
}
.ft th {
  position: sticky;
  top: 0;
  z-index: 1;
  padding: 4px 8px;
  background: var(--sunk);
  border-bottom: 1px solid var(--line);
  text-align: left;
  font-weight: 600;
  white-space: nowrap;
}
.ft td {
  padding: 4px 8px;
  border-bottom: 1px solid var(--sunk);
  vertical-align: middle;
  white-space: nowrap;
}
.c-n {
  width: 1%;
  font-family: var(--mono);
  font-weight: 600;
}
.c-t {
  width: 280px;
  max-width: 280px;
  overflow: hidden;
  text-overflow: ellipsis;
  font-family: var(--mono);
}
th.c-t {
  font-family: var(--ui);
}
.sch {
  color: var(--muted);
}
.c-r,
th.c-r {
  text-align: right;
  font-variant-numeric: tabular-nums;
}
.c-c {
  max-width: 300px;
  overflow: hidden;
  text-overflow: ellipsis;
}
.nm {
  margin-left: 6px;
  color: var(--muted);
  font-size: 12px;
}
.fr {
  cursor: pointer;
}
.fr:hover {
  background: color-mix(in srgb, var(--accent) 7%, transparent);
}
.fr.on {
  background: var(--sel);
  box-shadow: inset 3px 0 0 var(--accent);
}
.fr:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}
.fr.zero td {
  color: var(--muted);
}
.conf {
  display: inline-block;
  padding: 0 6px;
  border-radius: var(--r);
  font-size: 12px;
  line-height: 1.6;
  font-weight: 600;
  border: 1px solid;
}
.conf-certain {
  color: var(--ok);
  background: var(--ok-bg);
  border-style: solid;
}
.conf-inferred {
  color: var(--warn);
  background: var(--warn-bg);
  border-style: dashed;
}
.conf-ambiguous {
  color: var(--err);
  background: var(--err-bg);
  border-style: dotted;
  border-width: 2px;
}
.conf-unexplained {
  color: var(--muted);
  background: var(--sunk);
  border-style: double;
  border-width: 3px;
}
.flags {
  display: flex;
  gap: 4px;
  align-items: center;
}
.none {
  padding: 12px;
  color: var(--muted);
  text-align: center;
}
</style>
