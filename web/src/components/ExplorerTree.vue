<script setup lang="ts">
// 개체 탐색기: 카탈로그 스키마의 테이블을 스키마별 트리로 보이고, 마지막 미리보기에 걸린 테이블에 연산 뱃지를 붙인다.
import { computed } from 'vue'
import type { CatalogSchema } from '../api/types'
import type { TableBadge } from '../lib/summary'

const props = defineProps<{
  schema: CatalogSchema | null
  /** 스키마가 없는 이유(기준선 만드는 중, 409 등). 없으면 "불러오는 중" */
  schemaError: string | null
  /** "스키마.테이블" → 뱃지(lib/summary tableBadges) */
  badges: Map<string, TableBadge>
  /** aria-current 로 표시할 테이블("스키마.테이블") */
  currentTable: string | null
}>()

const emit = defineEmits<{
  select: [table: string]
  /** 마우스·포커스가 올라간 테이블(떠나면 null). 그래프가 그 노드의 경로를 강조한다. */
  hover: [table: string | null]
}>()

interface Group {
  schema: string
  open: boolean
  tables: { key: string; name: string; badge: TableBadge | undefined }[]
}

const groups = computed<Group[]>(() => {
  const by = new Map<string, Group>()
  for (const t of props.schema?.tables ?? []) {
    let g = by.get(t.schema)
    if (!g) {
      g = { schema: t.schema, open: false, tables: [] }
      by.set(t.schema, g)
    }
    const badge = props.badges.get(t.table)
    g.tables.push({ key: t.table, name: t.name, badge })
    if (badge || t.table === props.currentTable) g.open = true
  }
  const list = [...by.values()].sort((a, b) => a.schema.localeCompare(b.schema))
  for (const g of list) g.tables.sort((a, b) => a.name.localeCompare(b.name))
  // 걸린 테이블이 없으면 첫 스키마만 펼친다.
  if (list.length && !list.some((g) => g.open)) list[0].open = true
  return list
})

const tableCount = computed(() => props.schema?.tables.length ?? 0)
</script>

<template>
  <div class="ex">
    <div class="exh">
      <svg class="ic" viewBox="0 0 16 16" aria-hidden="true">
        <path d="M2.5 4.5c0-1.4 2.5-2.5 5.5-2.5s5.5 1.1 5.5 2.5S11 7 8 7 2.5 5.9 2.5 4.5zM2.5 4.5v7c0 1.4 2.5 2.5 5.5 2.5s5.5-1.1 5.5-2.5v-7" />
      </svg>
      <span class="db mono" translate="no" :title="schema?.database">{{ schema?.database ?? '데이터베이스' }}</span>
      <small>복제본</small>
    </div>
    <div class="treewrap">
      <p v-if="!schema" class="empty" role="status">
        {{ schemaError ?? '스키마를 불러오는 중…' }}
      </p>
      <ul v-else class="tree" aria-label="테이블">
        <li v-for="g in groups" :key="g.schema">
          <details :open="g.open">
            <summary>{{ g.schema }}</summary>
            <ul>
              <li v-for="t in g.tables" :key="t.key">
                <button
                  type="button"
                  class="it"
                  :title="t.key"
                  :aria-current="t.key === currentTable ? 'true' : undefined"
                  @click="emit('select', t.key)"
                  @mouseenter="emit('hover', t.key)"
                  @mouseleave="emit('hover', null)"
                  @focusin="emit('hover', t.key)"
                  @focusout="emit('hover', null)"
                >
                  <svg class="ic" viewBox="0 0 16 16" aria-hidden="true">
                    <rect x="2" y="3" width="12" height="10" rx="1.5" />
                    <path d="M2 7h12M6.5 7v6" />
                  </svg>
                  <span class="nm" translate="no">{{ t.name }}</span>
                  <em v-if="t.badge" class="pb" :class="`pb-${t.badge.kind}`">
                    {{ t.badge.text }}<span v-if="t.badge.srText" class="sr-only">{{ t.badge.srText }}</span>
                  </em>
                </button>
              </li>
            </ul>
          </details>
        </li>
      </ul>
    </div>
    <p v-if="schema" class="foot">테이블 {{ tableCount }}개 · 뱃지는 마지막 미리보기의 순변경</p>
  </div>
</template>

<style scoped>
.ex {
  display: flex;
  flex-direction: column;
  flex: 1;
  min-height: 0;
}
.exh {
  display: flex;
  align-items: center;
  gap: 8px;
  flex: none;
  height: 34px;
  padding: 0 var(--gx);
  background: var(--sunk);
  border-bottom: 1px solid var(--line);
  white-space: nowrap;
}
.exh .db {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  font-size: 13px;
  font-weight: 500;
}
.exh small {
  color: var(--muted);
  font-size: 12px;
}
.treewrap {
  flex: 1;
  min-height: 0;
  overflow: auto;
  overscroll-behavior: contain;
}
.empty {
  padding: 10px var(--gx);
  color: var(--muted);
}
.tree {
  list-style: none;
  margin: 0;
  padding: 4px 0 6px;
}
.tree ul {
  list-style: none;
  margin: 0;
  padding-left: 14px;
}
.tree summary,
.it {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
  padding: 3px var(--gx);
}
.tree summary {
  list-style: none;
  color: var(--muted);
  font-weight: 500;
  cursor: pointer;
}
.tree summary::-webkit-details-marker {
  display: none;
}
.tree summary::before {
  content: '';
  flex: none;
  width: 5px;
  height: 5px;
  margin: 0 3px 0 1px;
  border: solid currentColor;
  border-width: 0 1.5px 1.5px 0;
  transform: rotate(-45deg);
}
.tree details[open] > summary::before {
  transform: rotate(45deg) translate(-1px, -1px);
}
.it {
  width: 100%;
  border: 0;
  background: transparent;
  color: inherit;
  font: inherit;
  text-align: left;
  cursor: pointer;
}
.it:hover,
.tree summary:hover {
  background: color-mix(in srgb, var(--accent) 9%, transparent);
}
.tree summary:hover {
  color: var(--fg);
}
.it:focus-visible,
.tree summary:focus-visible {
  outline-offset: -2px;
}
.it[aria-current='true'] {
  background: color-mix(in srgb, var(--accent) 14%, transparent);
}
.it .ic {
  color: var(--muted);
}
.it[aria-current='true'] .ic {
  color: var(--accent);
}
.nm {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font: 13px var(--mono);
}
.it[aria-current='true'] .nm {
  font-weight: 700;
}
.pb {
  flex: none;
  padding: 0 5px;
  border-radius: var(--r);
  font: 600 12px / 1.5 var(--ui);
}
.pb-d {
  background: var(--del-bg);
  color: var(--del-fg);
}
.pb-u {
  background: var(--chg-bg);
  color: var(--fg);
}
.pb-i {
  background: var(--add-bg);
  color: var(--add-fg);
}
.pb-x {
  background: var(--err-bg);
  color: var(--err);
  border: 1px solid var(--err);
}
.pb-k {
  background: var(--warn-bg);
  color: var(--warn);
  border: 1px dashed var(--warn);
}
.foot {
  flex: none;
  padding: 6px var(--gx) 10px;
  border-top: 1px solid var(--line);
  color: var(--muted);
  font-size: 12px;
}
@media (prefers-reduced-motion: no-preference) {
  .it,
  .tree summary {
    transition: background-color var(--m-out) var(--ease);
  }
  .tree summary::before {
    transition: transform var(--m-in) var(--ease);
  }
}
</style>
