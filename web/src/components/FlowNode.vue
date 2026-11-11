<script setup lang="ts">
// 그래프 노드 6종(statement, firing, trigger, residual, error, group)을 한 컴포넌트에서 그린다.
import { Handle, Position } from '@vue-flow/core'
import { computed } from 'vue'
import { RESIDUAL_CATEGORY_LABEL, STATEMENT_STATUS_LABEL } from '../lib/format'
import type { RippleNodeData } from '../lib/graphModel'
import { describeNode } from '../lib/graphModel'
import { NODE_H, NODE_W } from '../lib/graphLayout'

defineOptions({ inheritAttrs: false })

const props = defineProps<{
  id: string
  data: RippleNodeData
  selected?: boolean
}>()

const n = computed(() => props.data.node)
const flags = computed(() => props.data.flags)
const title = computed(() => describeNode(n.value))
const kindLabel = computed(() => {
  switch (n.value.type) {
    case 'statement':
      return '문장'
    case 'firing':
      return '변경 묶음'
    case 'trigger':
      return '트리거'
    case 'residual':
      return '잔차'
    case 'error':
      return '오류'
    case 'group':
      return '접힌 묶음'
  }
  return ''
})
</script>

<template>
  <div
    class="rnode"
    :class="[
      `t-${n.type}`,
      { sel: selected, unobserved: n.type === 'trigger' && !n.observed },
    ]"
    :style="{ width: `${NODE_W}px`, minHeight: `${NODE_H - 16}px` }"
    :title="title"
    :aria-label="title"
  >
    <Handle type="target" :position="Position.Left" :connectable="false" />
    <div class="kind">{{ kindLabel }}</div>

    <template v-if="n.type === 'statement'">
      <div class="main mono">{{ n.label }}</div>
      <div class="row">
        <span class="chip" :class="n.status === 'ok' ? 'chip-ok' : 'chip-err'">{{ STATEMENT_STATUS_LABEL[n.status] }}</span>
        <span class="chip" :class="{ 'is-zero': n.rowsAffected === 0 }">
          {{ n.rowsAffected === null ? '행 수 모름' : `${n.rowsAffected}행` }}
        </span>
        <span class="muted">#{{ n.statementIndex }}</span>
      </div>
    </template>

    <template v-else-if="n.type === 'firing'">
      <div class="main">{{ n.table }}</div>
      <div class="row">
        <span class="chip" :class="`op-${n.operation.toLowerCase()}`">{{ n.operation }}</span>
        <span class="chip" :class="{ 'is-zero': flags.zeroRows }">{{ n.rowCount }}행</span>
        <span class="muted">깊이 {{ n.depth }} · #{{ n.captureSeq }}</span>
      </div>
      <div v-if="flags.zeroRows || flags.partial || flags.auditNotLast" class="row flags">
        <span v-if="flags.zeroRows" class="chip chip-info" title="영향 받은 행이 0개(헤더만 남음)">0행</span>
        <span v-if="flags.partial" class="chip chip-warn" title="행 이미지 없이 키만 수집됨(KEYS). 행 값은 보이지 않을 수 있습니다.">
          일부만 수집
        </span>
        <span v-if="flags.auditNotLast" class="chip chip-warn" title="이 테이블에 사용자 Last 트리거가 있어 감사 트리거 순서를 보장하지 못함(NONE). 자식 귀속이 모호할 수 있습니다.">
          감사 순서 불확실
        </span>
      </div>
    </template>

    <template v-else-if="n.type === 'trigger'">
      <div class="main">{{ n.trigger }}</div>
      <div class="row">
        <span class="chip" :class="n.observed ? 'chip-ok' : ''">
          {{ n.observed ? '관측됨' : '관측 안 됨(추정)' }}
        </span>
        <span class="muted">문장 #{{ n.statementIndex }}</span>
      </div>
    </template>

    <template v-else-if="n.type === 'residual'">
      <div class="main">{{ n.table }}</div>
      <div class="row">
        <span class="chip chip-warn">{{ RESIDUAL_CATEGORY_LABEL[n.category] }}</span>
      </div>
    </template>

    <template v-else-if="n.type === 'error'">
      <div class="main">오류 {{ n.errorNumber }}</div>
      <div class="msg">{{ n.message }}</div>
      <div v-if="n.isToolFault" class="row"><span class="chip chip-err">도구 오류</span></div>
    </template>

    <template v-else-if="n.type === 'group'">
      <div class="main">묶음 {{ n.members.length }}개</div>
      <div class="row">
        <span class="chip">{{ n.rowCount }}행</span>
        <span class="muted">같은 테이블·연산·깊이</span>
      </div>
    </template>

    <Handle type="source" :position="Position.Right" :connectable="false" />
  </div>
</template>

<style scoped>
.rnode {
  padding: 6px 10px 8px;
  background: var(--bg-elev);
  border: 1.5px solid var(--border);
  border-left-width: 5px;
  border-radius: var(--radius);
  color: var(--fg);
  font-size: 12px;
  cursor: pointer;
  overflow: hidden;
}
.rnode:hover {
  border-color: var(--accent);
}
.rnode.sel {
  outline: 2px solid var(--accent);
  outline-offset: 1px;
}
.kind {
  font-size: 11px;
  color: var(--fg-muted);
  font-weight: 600;
}
.main {
  font-weight: 650;
  font-size: 12.5px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.main.mono {
  font-weight: 500;
}
.msg {
  display: -webkit-box;
  -webkit-line-clamp: 2;
  line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
  color: var(--err);
}
.row {
  display: flex;
  align-items: center;
  gap: 5px;
  flex-wrap: wrap;
  margin-top: 3px;
}
.row.flags {
  margin-top: 2px;
}
.chip.is-zero {
  color: var(--fg-muted);
  font-weight: 500;
}
.chip.op-insert {
  background: var(--ok-bg);
  color: var(--ok);
  border-color: transparent;
}
.chip.op-update {
  background: var(--info-bg);
  color: var(--info);
  border-color: transparent;
}
.chip.op-delete {
  background: var(--err-bg);
  color: var(--err);
  border-color: transparent;
}

/* 노드 종류는 왼쪽 띠 색과 위의 글자("문장" 등)로 함께 구분한다 */
.t-statement {
  border-left-color: #4a86e8;
}
.t-firing {
  border-left-color: #3fae5f;
}
.t-trigger {
  border-left-color: #8791a0;
}
.t-residual {
  border-left-color: #d9902b;
}
.t-error {
  border-left-color: #dc4b44;
}
.t-group {
  border-left-color: #a463d8;
  border-style: double;
  border-width: 4px;
}
/* observed:false → 점선 테두리, 흐리게 */
.unobserved {
  border-style: dashed;
  opacity: 0.72;
}
:deep(.vue-flow__handle) {
  width: 6px;
  height: 6px;
  min-width: 0;
  min-height: 0;
  background: var(--fg-muted);
  border: none;
}
</style>
