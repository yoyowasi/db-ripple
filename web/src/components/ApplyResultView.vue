<script setup lang="ts">
// 적용 결과 패널(레일 본문 맨 위). 상태별로: 결과 배너, 선행 조건 실패, 달라진 원본 행(드리프트), 불일치,
// 미리보기 대 실제 건수, 적용 내용(완료 시각·IDENTITY 짝·비교 제외 열), 다음 안내.
import { computed } from 'vue'
import type { ApplyResult, ApplyStatus, DriftRow, MismatchKind, PreviewResult, Values } from '../api/types'
import { PRECONDITION_LABEL, formatCount, formatMs, formatTime, keyText } from '../lib/format'
import { netTotals } from '../lib/summary'
import { useSandboxStore } from '../stores/sandbox'
import NetChangeList from './NetChangeList.vue'
import { mergeNetRows, rowOpText, splitTable } from './rail/rowOps'

const props = defineProps<{ result: ApplyResult; preview: PreviewResult | null }>()

const TITLE: Record<ApplyStatus, string> = {
  committed: '원본에 적용됨',
  rolled_back_mismatch: '롤백됨 · 결과 불일치',
  drift_detected: '드리프트 감지',
  precondition_failed: '선행 조건 실패',
  error: '적용 중 오류',
}
const BANNER: Record<ApplyStatus, { cls: string; text: string }> = {
  committed: { cls: 'banner-ok', text: '원본에 적용했습니다(COMMIT)' },
  rolled_back_mismatch: { cls: 'banner-err', text: '적용하지 않음 · 결과가 달라 롤백했습니다(원본은 바뀌지 않았습니다)' },
  drift_detected: { cls: 'banner-err', text: '적용하지 않음 · 원본은 바뀌지 않았습니다(롤백)' },
  precondition_failed: { cls: 'banner-warn', text: '적용하지 않음 · 선행 조건을 만족하지 못했습니다(원본은 바뀌지 않았습니다)' },
  error: { cls: 'banner-err', text: '오류로 롤백했습니다' },
}
const NEXT: Record<ApplyStatus, string> = {
  committed: '이 미리보기는 서버에서 지워졌습니다. 다시 적용하려면 새 미리보기를 만드세요.',
  rolled_back_mismatch: '아래 불일치를 확인하고 새 미리보기를 만드세요.',
  drift_detected: '샌드박스를 새로 고친 뒤 다시 미리보기를 실행하세요.',
  precondition_failed: '아래 이유를 해결한 뒤 다시 시도하세요.',
  error: '원본에 남은 변경이 없는지 확인한 뒤 새 미리보기를 만드세요.',
}
const ORIGIN: Record<ApplyStatus, string> = {
  committed: '변경이 확정(COMMIT)됐습니다',
  rolled_back_mismatch: '바뀌지 않았습니다(롤백)',
  drift_detected: '바뀌지 않았습니다(롤백)',
  precondition_failed: '바뀌지 않았습니다(실행 안 함)',
  error: '롤백했습니다',
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

const sandbox = useSandboxStore()
const v = computed(() => props.result.verification)
/** 분리 모드에서 커밋됐는데 샌드박스 새로 고침이 시작되지 않았다: 기준선이 오래됐다. */
const staleAfterCommit = computed(
  () => props.result.status === 'committed' && !props.result.sandboxRefreshStarted && sandbox.status?.mode === 'split',
)
const isKeyset = computed(() => v.value.mode === 'KEYSET')
const totalMs = computed(() => Object.values(props.result.timingsMs).reduce((a, b) => a + b, 0))

/** 표 안에서 점 뒤에서 줄바꿈할 수 있게 한다. */
const brk = (s: string) => s.replace(/\./g, '.​')
const val = (x: string | null | undefined) => (x === null || x === undefined ? 'NULL' : x)

function driftCols(d: DriftRow): string[] {
  if (d.columns.length) return d.columns
  return Object.keys(d.expected ?? d.actual ?? {})
}
function at(values: Values | null, c: string): string {
  return values ? val(values[c]) : '(없음)'
}

const reason = computed(() =>
  props.result.status === 'drift_detected' && v.value.driftRows.length
    ? `미리보기 뒤 원본의 대상 행 ${formatCount(v.value.driftRows.length)}건이 바뀌어 실행하지 않고 롤백했습니다.`
    : null,
)

// 미리보기 건수 대 실제 건수(테이블당 한 줄)
const compare = computed(() => {
  const act = mergeNetRows(v.value.actualNetSummary)
  if (!act.length) return []
  const pre = mergeNetRows(props.preview?.netSummary ?? [])
  const tables = [...new Set([...pre.map((r) => r.table), ...act.map((r) => r.table)])]
  return tables
    .map((table) => {
      const p = pre.find((r) => r.table === table)
      const a = act.find((r) => r.table === table)
      const pt = netTotals(p ? [p] : [])
      const at = netTotals(a ? [a] : [])
      // 합계만이 아니라 추가·수정·삭제 종류별로 비교한다(수정 2 대 추가 1+삭제 1은 불일치).
      const same = pt.inserted === at.inserted && pt.updated === at.updated && pt.deleted === at.deleted
      const op = !same && p && a && pt.total > 0 && at.total > 0 ? `${rowOpText(p)} → ${rowOpText(a)}` : rowOpText((a ?? p)!)
      return { table, ...splitTable(table), op, pn: pt.total, an: at.total, same }
    })
    .filter((r) => r.pn > 0 || r.an > 0)
})
const preTotal = computed(() => netTotals(props.preview?.netSummary ?? []).total)
const actTotal = computed(() => netTotals(v.value.actualNetSummary).total)
const matchLine = computed(() => {
  const m = v.value.netSummaryMatch
  if (m === null) return { cls: 'nil', text: '건수는 비교하지 않았습니다' }
  return m
    ? { cls: 'ok', text: `건수 일치 · ${formatCount(preTotal.value)}행 = ${formatCount(actTotal.value)}행` }
    : { cls: 'no', text: `건수 불일치 · 미리보기 ${formatCount(preTotal.value)}행 ≠ 실제 ${formatCount(actTotal.value)}행` }
})

// "스키마.테이블.열" → 테이블별로 묶는다.
const volatileByTable = computed(() => {
  const by = new Map<string, string[]>()
  for (const s of v.value.volatileIgnored) {
    const i = s.lastIndexOf('.')
    const t = i < 0 ? '' : s.slice(0, i)
    by.set(t, [...(by.get(t) ?? []), i < 0 ? s : s.slice(i + 1)])
  }
  return [...by].map(([t, cols]) => ({ t, cols }))
})
function samePair(p: { previewKey: Parameters<typeof keyText>[0]; actualKey: Parameters<typeof keyText>[0] }): boolean {
  return keyText(p.previewKey) === keyText(p.actualKey)
}
</script>

<template>
  <section class="res" aria-labelledby="h-apply-res">
    <header class="sec">
      <h3 id="h-apply-res" class="title" tabindex="-1">
        {{ TITLE[result.status] }}
        <code class="code">{{ result.status }}</code>
      </h3>
      <div class="banner" :class="BANNER[result.status].cls" :role="result.status === 'committed' ? 'status' : 'alert'">
        <svg v-if="result.status === 'committed'" class="ic" viewBox="0 0 16 16" aria-hidden="true"><path d="M3 8.5l3.2 3.2L13 4.8" /></svg>
        <svg v-else class="ic" viewBox="0 0 16 16" aria-hidden="true"><path d="M4 4l8 8M12 4l-8 8" /></svg>
        <div>
          <strong>{{ BANNER[result.status].text }}</strong>
          <span v-if="result.message" class="msg">서버 메시지: {{ result.message }}</span>
        </div>
      </div>
      <div v-if="staleAfterCommit" class="banner banner-warn gap" role="status">
        <svg class="ic" viewBox="0 0 16 16" aria-hidden="true"><path d="M8 2.2L14.2 13H1.8z" /><path d="M8 6.6v3M8 11.4v.1" /></svg>
        <div>
          <strong>기준선이 오래됨</strong>
          원본은 바뀌었지만 샌드박스 새로 고침이 시작되지 않았습니다. 새로 고친 뒤 다시 미리보기를 실행하세요.
        </div>
      </div>
      <div v-if="v.residualCheckFailed" class="banner banner-err gap" role="alert">
        <svg class="ic" viewBox="0 0 16 16" aria-hidden="true"><circle cx="8" cy="8" r="5.8" /><path d="M4 12L12 4" /></svg>
        <div>
          <strong>잔여 확인 실패</strong>
          롤백 뒤 원본에 남은 변경을 확인하지 못했습니다. 원본이 바뀌지 않았다고 확정할 수 없으니 직접 확인하세요.
        </div>
      </div>
      <div v-if="isKeyset" class="banner banner-warn gap">
        <svg class="ic" viewBox="0 0 16 16" aria-hidden="true"><path d="M8 2.2L14.2 13H1.8z" /><path d="M8 6.6v3M8 11.4v.1" /></svg>
        <div>
          <strong>KEYSET 검증(제한된 검증)</strong>
          키 집합과 테이블 건수만 비교했습니다. 값 수준 검증이 약합니다.
        </div>
      </div>
    </header>

    <section v-if="result.preconditionFailures.length" class="sec" aria-labelledby="h-pre">
      <h3 id="h-pre">실행하지 못한 이유(선행 조건)</h3>
      <ul class="list">
        <li v-for="p in result.preconditionFailures" :key="p">
          <code>{{ p }}</code>
          <span class="sub">{{ PRECONDITION_LABEL[p] }}</span>
        </li>
      </ul>
    </section>

    <section v-if="v.driftRows.length" class="sec" aria-labelledby="h-drift">
      <h3 id="h-drift">달라진 원본 행(driftRows)</h3>
      <table class="drift">
        <caption class="sr-only">드리프트로 감지된 원본 행</caption>
        <thead>
          <tr>
            <th scope="col">테이블</th>
            <th scope="col">키</th>
            <th scope="col">열</th>
          </tr>
        </thead>
        <tbody v-for="(d, i) in v.driftRows" :key="i">
          <tr>
            <td>{{ brk(d.table) }}</td>
            <td class="mono">{{ brk(keyText(d.key)) }}</td>
            <td>
              <template v-if="d.kind === 'CHANGED'">{{ driftCols(d).join(', ') }}</template>
              <template v-else>{{ DRIFT_LABEL[d.kind] }}</template>
            </td>
          </tr>
          <tr>
            <td colspan="3" class="chg">
              <template v-if="d.kind === 'MULTISET_COUNT'">
                예상 → 실제
                <del><span class="sr-only">예상: </span>{{ d.expectedMultiplicity ?? '-' }}개</del> →
                <ins><span class="sr-only">실제: </span>{{ d.actualMultiplicity ?? '-' }}개</ins>
              </template>
              <template v-else-if="d.kind === 'MISSING'">
                예상 → 실제
                <del v-for="c in driftCols(d)" :key="c" class="cv"><span class="sr-only">예상: </span>{{ c }}={{ at(d.expected, c) }}</del>
                → <ins><span class="sr-only">실제: </span>행 없음</ins>
              </template>
              <template v-else>
                <span v-for="c in driftCols(d)" :key="c" class="cv">
                  <span class="cn">예상 → 실제<template v-if="driftCols(d).length > 1"> · {{ c }}</template></span>
                  <del><span class="sr-only">예상: </span>{{ at(d.expected, c) }}</del> →
                  <ins><span class="sr-only">실제: </span>{{ at(d.actual, c) }}</ins>
                </span>
              </template>
            </td>
          </tr>
        </tbody>
      </table>
      <p class="hint">예상 = 미리보기 때 읽은 값, 실제 = 적용 직전 원본의 값입니다.</p>
    </section>

    <section v-if="v.mismatches.length" class="sec" aria-labelledby="h-mm">
      <h3 id="h-mm">불일치({{ formatCount(v.mismatches.length) }}건)</h3>
      <ul class="list">
        <li v-for="(m, i) in v.mismatches" :key="i">
          <b>{{ MISMATCH_LABEL[m.kind] }}</b>
          <span class="sub">
            <template v-if="m.statementIndex !== null">문장 #{{ m.statementIndex }} </template>
            <template v-if="m.table">{{ m.table }} </template>
            <span v-if="m.key" class="mono">{{ keyText(m.key) }} </span>
            <template v-if="m.column">.{{ m.column }}</template>
          </span>
          <span v-if="m.expected !== null || m.actual !== null" class="sub mono">
            예상 {{ m.expected ?? '-' }} → 실제 {{ m.actual ?? '-' }}
          </span>
          <span class="sub">{{ m.message }}</span>
        </li>
      </ul>
    </section>

    <section v-if="compare.length" class="sec" aria-labelledby="h-cmp">
      <h3 id="h-cmp">미리보기 건수와 실제 건수 대조</h3>
      <table class="cmp">
        <caption class="sr-only">테이블별 미리보기 건수와 실제 건수</caption>
        <thead>
          <tr>
            <th scope="col">테이블</th>
            <th scope="col">변경</th>
            <th scope="col" class="num">미리보기</th>
            <th scope="col" class="num">실제</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="r in compare" :key="r.table">
            <th scope="row" :title="r.table">{{ r.name }}</th>
            <td>{{ r.op }}</td>
            <td class="num">{{ formatCount(r.pn) }}</td>
            <td class="num">
              {{ formatCount(r.an) }}
              <svg v-if="r.same" class="ic ok" viewBox="0 0 16 16" aria-hidden="true"><path d="M3 8.5l3.2 3.2L13 4.8" /></svg>
              <svg v-else class="ic no" viewBox="0 0 16 16" aria-hidden="true"><path d="M4 4l8 8M12 4l-8 8" /></svg>
              <span class="sr-only">{{ r.same ? '일치' : '불일치' }}</span>
            </td>
          </tr>
        </tbody>
      </table>
      <p class="vline" :class="matchLine.cls">
        <svg v-if="matchLine.cls === 'ok'" class="ic" viewBox="0 0 16 16" aria-hidden="true"><path d="M3 8.5l3.2 3.2L13 4.8" /></svg>
        <svg v-else-if="matchLine.cls === 'no'" class="ic" viewBox="0 0 16 16" aria-hidden="true"><path d="M4 4l8 8M12 4l-8 8" /></svg>
        <svg v-else class="ic" viewBox="0 0 16 16" aria-hidden="true"><circle cx="8" cy="8" r="5.8" /><path d="M8 7.4v3.6M8 5v.1" /></svg>
        <span>{{ matchLine.text }}</span>
      </p>
    </section>

    <section class="sec" aria-labelledby="h-info">
      <h3 id="h-info">결과</h3>
      <dl class="rd">
        <dt>원본</dt>
        <dd><b>{{ ORIGIN[result.status] }}</b></dd>
        <template v-if="reason">
          <dt>이유</dt>
          <dd>{{ reason }}</dd>
        </template>
        <dt>완료 시각</dt>
        <dd><time :datetime="result.finishedAt">{{ formatTime(result.finishedAt) }}</time> · 소요 {{ formatMs(totalMs) }}</dd>
        <dt>비교 방식</dt>
        <dd><code>{{ v.mode }}</code></dd>
        <template v-if="v.identityPairs.length">
          <dt>IDENTITY 짝</dt>
          <dd>
            {{ formatCount(v.identityPairs.length) }}건
            <ul class="list tight">
              <li v-for="(p, i) in v.identityPairs" :key="i">
                {{ p.table }} <span class="mono">{{ keyText(p.previewKey) }}</span> → 실제
                <span class="mono">{{ keyText(p.actualKey) }}</span><template v-if="samePair(p)">(같은 값)</template>
              </li>
            </ul>
          </dd>
        </template>
        <template v-if="volatileByTable.length">
          <dt>비교에서 제외</dt>
          <dd>
            <span v-for="g in volatileByTable" :key="g.t" class="blk">{{ g.t }}의 {{ g.cols.join('·') }}(예상값)</span>
          </dd>
        </template>
        <dt>샌드박스</dt>
        <dd>새로 고침을 {{ result.sandboxRefreshStarted ? '시작함' : '시작하지 않음' }}</dd>
        <dt>다음</dt>
        <dd>{{ NEXT[result.status] }}</dd>
      </dl>
      <ul v-if="!isKeyset && v.limitations.length" class="list tight lim" aria-label="검증의 한계">
        <li v-for="(l, i) in v.limitations" :key="i">한계: {{ l }}</li>
      </ul>
      <ul v-else-if="v.limitations.length" class="list tight lim">
        <li v-for="(l, i) in v.limitations" :key="i">{{ l }}</li>
      </ul>
    </section>

    <section v-if="v.residualCommitted.length" class="sec" aria-labelledby="h-rc">
      <h3 id="h-rc">롤백 뒤 남은 변경 {{ formatCount(v.residualCommitted.length) }}건</h3>
      <NetChangeList :items="v.residualCommitted" />
    </section>

    <section v-if="v.statementRowCounts.length || v.tableCounts.length" class="sec">
      <details class="det">
        <summary>검증 상세(문장·테이블 행 수)</summary>
        <table v-if="v.statementRowCounts.length" class="cmp">
          <caption>문장별 영향 행 수</caption>
          <thead>
            <tr>
              <th scope="col" class="num">#</th>
              <th scope="col" class="num">미리보기</th>
              <th scope="col" class="num">실제</th>
              <th scope="col">비교</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="c in v.statementRowCounts" :key="c.index">
              <td class="num">{{ c.index }}</td>
              <td class="num">{{ c.preview === null ? '-' : formatCount(c.preview) }}</td>
              <td class="num">{{ c.actual === null ? '-' : formatCount(c.actual) }}</td>
              <td>
                <span class="chip" :class="c.preview !== c.actual ? 'chip-err' : 'chip-ok'">{{ c.preview !== c.actual ? '다름' : '같음' }}</span>
              </td>
            </tr>
          </tbody>
        </table>
        <table v-if="v.tableCounts.length" class="cmp">
          <caption>테이블 행 수 변화</caption>
          <thead>
            <tr>
              <th scope="col">테이블</th>
              <th scope="col" class="num">전 → 후</th>
              <th scope="col" class="num">변화</th>
              <th scope="col" class="num">예상</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="t in v.tableCounts" :key="t.table">
              <th scope="row" :title="t.table">{{ splitTable(t.table).name }}</th>
              <td class="num">{{ formatCount(t.before) }} → {{ formatCount(t.after) }}</td>
              <td class="num">
                {{ formatCount(t.after - t.before) }}
                <span v-if="t.after - t.before !== t.expectedDelta" class="chip chip-err">다름</span>
              </td>
              <td class="num">{{ formatCount(t.expectedDelta) }}</td>
            </tr>
          </tbody>
        </table>
        <dl class="kv">
          <dt>스냅샷</dt><dd>{{ formatMs(result.timingsMs.snapshot) }}</dd>
          <dt>드리프트 검사</dt><dd>{{ formatMs(result.timingsMs.drift) }}</dd>
          <dt>실행</dt><dd>{{ formatMs(result.timingsMs.execute) }}</dd>
          <dt>최종 차이</dt><dd>{{ formatMs(result.timingsMs.netDiff) }}</dd>
          <dt>커밋</dt><dd>{{ formatMs(result.timingsMs.commit) }}</dd>
        </dl>
      </details>
    </section>
  </section>
</template>

<style scoped>
.res {
  min-width: 0;
  border-bottom: 1px solid var(--tick);
}
.sec {
  min-width: 0;
  padding: 8px var(--gx) 12px;
  border-bottom: 1px solid var(--line);
}
.sec:last-child {
  border-bottom: 0;
}
.sec > h3 {
  margin-bottom: 6px;
}
.sec > h3.title {
  display: flex;
  flex-wrap: wrap;
  align-items: baseline;
  gap: 2px 8px;
  margin-bottom: 8px;
  font: 600 16px / 1.3 var(--ui);
  color: var(--fg);
}
.sec > h3.title:focus {
  outline: none;
}
.sec > h3.title:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}
.code {
  font: 12px var(--mono);
  color: var(--muted);
}
.gap {
  margin-top: 6px;
}
.msg {
  display: block;
  margin-top: 2px;
  font-weight: 400;
  color: var(--muted);
  overflow-wrap: anywhere;
}
.list {
  display: grid;
  gap: 6px;
  margin: 0;
  padding: 0;
  list-style: none;
  font-size: 12px;
  overflow-wrap: anywhere;
}
.list.tight {
  gap: 2px;
  margin-top: 2px;
}
.list.lim {
  margin-top: 8px;
  color: var(--muted);
}
.sub {
  display: block;
  color: var(--muted);
}

/* 드리프트: 한 건 = 두 줄(테이블·키·열 / 예상 → 실제) */
.drift,
.cmp {
  width: 100%;
  table-layout: auto;
  border-collapse: collapse;
  font-size: 12px;
}
.drift th,
.drift td {
  padding: 3px 6px;
  border: 1px solid var(--line);
  text-align: left;
  vertical-align: top;
  overflow-wrap: anywhere;
}
.drift thead th {
  background: var(--panel);
  font-weight: 600;
}
.drift td.chg {
  background: var(--chg-bg);
  color: var(--fg);
}
.cv {
  display: block;
}
.cv + .cv {
  margin-top: 2px;
}
.cn {
  display: block;
  color: var(--muted);
}
del {
  background: var(--del-bg);
  color: var(--del-fg);
  text-decoration: line-through;
  text-decoration-thickness: 1px;
}
ins {
  background: var(--add-bg);
  color: var(--add-fg);
  text-decoration: none;
}
.cmp caption {
  padding: 4px 0;
  text-align: left;
  font-weight: 600;
  color: var(--muted);
}
.cmp th,
.cmp td {
  padding: 3px 4px;
  border-bottom: 1px solid var(--line);
  text-align: left;
  vertical-align: top;
  overflow-wrap: anywhere;
}
.cmp thead th {
  color: var(--muted);
  font-weight: 500;
}
.cmp tbody th {
  font-weight: 500;
  font-family: var(--mono);
}
.cmp .num {
  text-align: right;
  font-variant-numeric: tabular-nums;
}
.cmp .ic.ok {
  color: var(--ok);
}
.cmp .ic.no {
  color: var(--err);
}
.vline {
  display: flex;
  gap: 6px;
  align-items: flex-start;
  margin-top: 6px;
  padding: 5px 8px;
  border: 1px solid;
  border-radius: var(--r);
  font-weight: 600;
}
.vline .ic {
  margin-top: 3px;
}
.vline.ok {
  color: var(--ok);
  background: var(--ok-bg);
  border-color: var(--ok);
}
.vline.no {
  color: var(--err);
  background: var(--err-bg);
  border-color: var(--err);
}
.vline.nil {
  color: var(--muted);
  background: var(--panel);
  border-color: var(--ctl);
}

.rd {
  display: grid;
  grid-template-columns: 84px minmax(0, 1fr);
  gap: 5px 8px;
  margin: 0;
}
.rd dt {
  color: var(--muted);
}
.rd dd {
  margin: 0;
  min-width: 0;
  overflow-wrap: anywhere;
}
.blk {
  display: block;
}
.det > summary {
  cursor: pointer;
  color: var(--muted);
  font-weight: 600;
}
.det > summary:hover {
  color: var(--fg);
}
.det table {
  margin-top: 6px;
}
.det .kv {
  margin-top: 8px;
}
</style>
