<script setup lang="ts">
// ApplyResult 표시(C4): 상태, 선행 조건, 드리프트, 불일치, 문장 행 수, 테이블 건수, IDENTITY 짝, KEYSET 한계, 잔여 확인 실패.
import { computed } from 'vue'
import type { ApplyResult, ApplyStatus, DriftRow, MismatchKind, Values } from '../api/types'
import {
  APPLY_STATUS_LABEL,
  PRECONDITION_LABEL,
  formatCount,
  formatMs,
  formatTime,
  keyText,
} from '../lib/format'
import NetChangeList from './NetChangeList.vue'

const props = defineProps<{ result: ApplyResult }>()

const BANNER: Record<ApplyStatus, string> = {
  committed: 'banner-ok',
  rolled_back_mismatch: 'banner-err',
  drift_detected: 'banner-warn',
  precondition_failed: 'banner-warn',
  error: 'banner-err',
}

const EXPLAIN: Record<ApplyStatus, string> = {
  committed: '미리보기와 같은 결과임을 확인한 뒤 원본에 COMMIT했습니다. 이 미리보기는 서버에서 지워졌습니다.',
  rolled_back_mismatch:
    '실행해 보니 결과가 미리보기와 달라 COMMIT하지 않고 롤백했습니다. 원본은 바뀌지 않았습니다. 아래 불일치를 확인하고 새 미리보기를 만드세요.',
  drift_detected:
    '미리보기를 만든 뒤 원본의 대상 행이 다른 곳에서 바뀌었습니다. 적용하지 않고 롤백했습니다. 새로 고침된 기준선으로 새 미리보기를 만드세요.',
  precondition_failed: '선행 조건을 만족하지 못해 실행하지 않았습니다. 원본은 바뀌지 않았습니다.',
  error: '적용 중 오류가 나서 롤백했습니다. 원본에 남은 변경이 있는지는 아래 "롤백 뒤 남은 변경"을 확인하세요.',
}

const MISMATCH_LABEL: Record<MismatchKind, string> = {
  STATEMENT_ROW_COUNT: '문장 영향 행 수',
  NET_SUMMARY: '테이블별 건수',
  ROW_VALUE: '행 값',
  ROW_MISSING: '예상한 행이 없음',
  ROW_UNEXPECTED: '예상하지 못한 행',
  IDENTITY_UNPAIRED: 'IDENTITY 짝을 못 찾음',
  UNEXPECTED_TABLE: '예상 밖 테이블 변경',
  VOLATILE_FORMAT: '예상값 형식 불일치',
  KEYSET_TABLE_COUNT: 'KEYSET 테이블 건수',
}

const DRIFT_LABEL: Record<DriftRow['kind'], string> = {
  CHANGED: '값이 바뀜',
  MISSING: '행이 사라짐',
  MULTISET_COUNT: '같은 행 개수가 다름',
}

const v = computed(() => props.result.verification)
const isKeyset = computed(() => v.value.mode === 'KEYSET')

function cols(values: Values | null, columns: string[]): string {
  if (!values) return '(없음)'
  const use = columns.length ? columns : Object.keys(values)
  return use.map((c) => `${c}=${values[c] ?? 'NULL'}`).join(', ')
}

function matchChip(m: boolean | null): { text: string; cls: string } {
  if (m === null) return { text: '비교 안 함', cls: '' }
  return m ? { text: '일치', cls: 'chip-ok' } : { text: '불일치', cls: 'chip-err' }
}
const netMatch = computed(() => matchChip(v.value.netSummaryMatch))
</script>

<template>
  <div class="result" aria-live="polite">
    <div class="banner" :class="BANNER[result.status]" :role="result.status === 'committed' ? 'status' : 'alert'">
      <div>
        <strong>{{ APPLY_STATUS_LABEL[result.status] }}</strong>
        {{ EXPLAIN[result.status] }}
        <span v-if="result.message" class="msg">서버 메시지: {{ result.message }}</span>
        <span class="msg muted">{{ formatTime(result.finishedAt) }}
          <template v-if="result.status === 'committed'">
            · 샌드박스 새로 고침 {{ result.sandboxRefreshStarted ? '시작함' : '시작하지 않음' }}
          </template>
        </span>
      </div>
    </div>

    <ul v-if="result.preconditionFailures.length" class="plain">
      <li v-for="p in result.preconditionFailures" :key="p"><code>{{ p }}</code> {{ PRECONDITION_LABEL[p] }}</li>
    </ul>

    <!-- 잔여 확인 실패(적용 중 롤백 뒤 남은 변경 검사가 끝나지 않음) -->
    <div v-if="v.residualCheckFailed" class="banner banner-err" role="alert">
      <div>
        <strong>잔여 확인 실패</strong>
        롤백 뒤 원본에 남은 변경을 확인하지 못했습니다. 원본이 바뀌지 않았다고 확정할 수 없으니 직접 확인하세요.
      </div>
    </div>

    <div v-if="isKeyset" class="banner banner-warn">
      <div>
        <strong>KEYSET 검증(제한된 검증)</strong>
        원본 스냅샷을 만들 수 없어(또는 강제 설정) 키 집합과 테이블 건수만 비교했습니다. 값 수준 검증이 약합니다.
        <ul v-if="v.limitations.length" class="plain tight">
          <li v-for="(l, i) in v.limitations" :key="i">{{ l }}</li>
        </ul>
      </div>
    </div>
    <ul v-else-if="v.limitations.length" class="plain">
      <li v-for="(l, i) in v.limitations" :key="i" class="muted">한계: {{ l }}</li>
    </ul>

    <dl class="kv">
      <dt>검증 방식</dt>
      <dd>{{ v.mode === 'KEYSET' ? 'KEYSET(키 집합 비교)' : '스냅샷 최종 차이 비교' }}</dd>
      <dt>테이블별 건수</dt>
      <dd><span class="chip" :class="netMatch.cls">{{ netMatch.text }}</span></dd>
    </dl>

    <section v-if="v.driftRows.length">
      <h3>드리프트(미리보기 뒤 원본이 바뀐 행)</h3>
      <table class="data">
        <thead>
          <tr><th>테이블</th><th>키</th><th>종류</th><th>미리보기 기준</th><th>지금 원본</th></tr>
        </thead>
        <tbody>
          <tr v-for="(d, i) in v.driftRows" :key="i">
            <td>{{ d.table }}</td>
            <td class="mono">{{ keyText(d.key) }}</td>
            <td>{{ DRIFT_LABEL[d.kind] }}</td>
            <template v-if="d.kind === 'MULTISET_COUNT'">
              <td>{{ d.expectedMultiplicity ?? '-' }}개</td>
              <td>{{ d.actualMultiplicity ?? '-' }}개</td>
            </template>
            <template v-else>
              <td class="mono">{{ cols(d.expected, d.columns) }}</td>
              <td class="mono">{{ d.kind === 'MISSING' ? '(행 없음)' : cols(d.actual, d.columns) }}</td>
            </template>
          </tr>
        </tbody>
      </table>
    </section>

    <section v-if="v.mismatches.length">
      <h3>불일치({{ v.mismatches.length }}건)</h3>
      <table class="data">
        <thead>
          <tr><th>종류</th><th>대상</th><th>예상</th><th>실제</th><th>설명</th></tr>
        </thead>
        <tbody>
          <tr v-for="(m, i) in v.mismatches" :key="i">
            <td>{{ MISMATCH_LABEL[m.kind] }}</td>
            <td>
              <template v-if="m.statementIndex !== null">문장 #{{ m.statementIndex }} </template>
              <template v-if="m.table">{{ m.table }} </template>
              <span v-if="m.key" class="mono">{{ keyText(m.key) }} </span>
              <template v-if="m.column">.{{ m.column }}</template>
            </td>
            <td class="mono">{{ m.expected ?? '-' }}</td>
            <td class="mono">{{ m.actual ?? '-' }}</td>
            <td>{{ m.message }}</td>
          </tr>
        </tbody>
      </table>
    </section>

    <section v-if="v.statementRowCounts.length">
      <h3>문장별 영향 행 수(미리보기 대 실제)</h3>
      <table class="data">
        <thead>
          <tr><th class="num">#</th><th class="num">미리보기</th><th class="num">실제</th><th></th></tr>
        </thead>
        <tbody>
          <tr v-for="c in v.statementRowCounts" :key="c.index">
            <td class="num">{{ c.index }}</td>
            <td class="num">{{ c.preview === null ? '-' : formatCount(c.preview) }}</td>
            <td class="num">{{ c.actual === null ? '-' : formatCount(c.actual) }}</td>
            <td>
              <span v-if="c.preview !== c.actual" class="chip chip-err">다름</span>
              <span v-else class="chip chip-ok">같음</span>
            </td>
          </tr>
        </tbody>
      </table>
    </section>

    <section v-if="v.tableCounts.length">
      <h3>테이블 행 수 변화</h3>
      <table class="data">
        <thead>
          <tr><th>테이블</th><th class="num">전</th><th class="num">후</th><th class="num">변화</th><th class="num">예상 변화</th><th></th></tr>
        </thead>
        <tbody>
          <tr v-for="t in v.tableCounts" :key="t.table">
            <td>{{ t.table }}</td>
            <td class="num">{{ formatCount(t.before) }}</td>
            <td class="num">{{ formatCount(t.after) }}</td>
            <td class="num">{{ formatCount(t.after - t.before) }}</td>
            <td class="num">{{ formatCount(t.expectedDelta) }}</td>
            <td>
              <span v-if="t.after - t.before !== t.expectedDelta" class="chip chip-err">다름</span>
            </td>
          </tr>
        </tbody>
      </table>
    </section>

    <section v-if="v.identityPairs.length">
      <h3>IDENTITY 짝(미리보기의 새 키 → 실제 새 키)</h3>
      <table class="data">
        <thead>
          <tr><th>테이블</th><th>미리보기 키</th><th>실제 키</th></tr>
        </thead>
        <tbody>
          <tr v-for="(p, i) in v.identityPairs" :key="i">
            <td>{{ p.table }}</td>
            <td class="mono">{{ keyText(p.previewKey) }}</td>
            <td class="mono">{{ keyText(p.actualKey) }}</td>
          </tr>
        </tbody>
      </table>
    </section>

    <section v-if="v.volatileIgnored.length">
      <h3>값 대신 존재·형식만 비교한 열(예상값)</h3>
      <p class="chips">
        <code v-for="c in v.volatileIgnored" :key="c" class="chip">{{ c }}</code>
      </p>
    </section>

    <section v-if="v.residualCommitted.length">
      <h3>롤백 뒤 남은 변경</h3>
      <NetChangeList :items="v.residualCommitted" />
    </section>

    <details v-if="v.actualNetSummary.length">
      <summary>실제 테이블별 건수</summary>
      <table class="data">
        <thead>
          <tr><th>테이블</th><th class="num">추가</th><th class="num">수정</th><th class="num">삭제</th><th class="num">제거</th><th class="num">추가(다중집합)</th></tr>
        </thead>
        <tbody>
          <tr v-for="r in v.actualNetSummary" :key="r.table">
            <td>{{ r.table }}</td>
            <td class="num">{{ r.inserted }}</td>
            <td class="num">{{ r.updated }}</td>
            <td class="num">{{ r.deleted }}</td>
            <td class="num">{{ r.multisetRemoved }}</td>
            <td class="num">{{ r.multisetAdded }}</td>
          </tr>
        </tbody>
      </table>
    </details>

    <details>
      <summary>단계별 소요 시간</summary>
      <dl class="kv">
        <dt>스냅샷</dt><dd>{{ formatMs(result.timingsMs.snapshot) }}</dd>
        <dt>드리프트 검사</dt><dd>{{ formatMs(result.timingsMs.drift) }}</dd>
        <dt>실행</dt><dd>{{ formatMs(result.timingsMs.execute) }}</dd>
        <dt>최종 차이</dt><dd>{{ formatMs(result.timingsMs.netDiff) }}</dd>
        <dt>커밋</dt><dd>{{ formatMs(result.timingsMs.commit) }}</dd>
      </dl>
    </details>
  </div>
</template>

<style scoped>
.result {
  display: grid;
  gap: 10px;
}
.msg {
  display: block;
  margin-top: 2px;
}
.plain {
  margin: 0;
  padding-left: 18px;
  font-size: 12.5px;
}
.plain.tight {
  margin-top: 4px;
}
.chips {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
  margin: 4px 0 0;
}
section h3 {
  margin-bottom: 4px;
}
summary {
  cursor: pointer;
  font-size: 12.5px;
  color: var(--fg-muted);
}
</style>
