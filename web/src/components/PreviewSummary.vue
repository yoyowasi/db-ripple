<script setup lang="ts">
// C4(요약 부분): 문장별 결과, 테이블별 건수, 경고, 롤백 뒤 남은 변경, 환경 지문, 수집 범위.
import { computed } from 'vue'
import { exportUrl } from '../api/client'
import type { PreviewResult, PreviewStatus } from '../api/types'
import {
  PREVIEW_STATUS_LABEL,
  STATEMENT_STATUS_LABEL,
  formatCount,
  formatMs,
  formatTime,
  shortHash,
} from '../lib/format'
import { useSandboxStore } from '../stores/sandbox'
import DiagnosticList from './DiagnosticList.vue'
import NetChangeList from './NetChangeList.vue'

const props = defineProps<{ preview: PreviewResult; consumed: boolean }>()

const sandbox = useSandboxStore()

const STATUS_CHIP: Record<PreviewStatus, string> = {
  ok: 'chip-ok',
  failed: 'chip-err',
  trigger_blocked: 'chip-err',
  truncated: 'chip-warn',
}
const STATUS_NOTE: Partial<Record<PreviewStatus, string>> = {
  failed: '문장이 실패했습니다. 트랜잭션은 롤백됐고 원본은 바뀌지 않았습니다. 오류는 아래 문장별 결과에 있습니다.',
  trigger_blocked:
    '트리거가 ROLLBACK 하거나 거부했습니다(E13). 트랜잭션 안 변경은 0건이고, 원본에 적용해도 같은 결과가 납니다. 거부한 트리거는 그래프의 오류·트리거 노드에서 확인하세요.',
  truncated:
    '변경 행이 수집 상한을 넘어 행 이미지를 일부만 모았습니다. 건수는 정확하지만 행 값을 다 볼 수 없어 [적용]을 막습니다(D8).',
}

const totals = computed(() => {
  const t = { inserted: 0, updated: 0, deleted: 0, removed: 0, added: 0 }
  for (const r of props.preview.netSummary) {
    t.inserted += r.inserted
    t.updated += r.updated
    t.deleted += r.deleted
    t.removed += r.multisetRemoved
    t.added += r.multisetAdded
  }
  return t
})
const hasMultiset = computed(() => totals.value.removed > 0 || totals.value.added > 0)
const fp = computed(() => props.preview.baseline.fingerprint)
const manifest = computed(() => sandbox.status?.manifest ?? null)
const timings = computed(() => Object.entries(props.preview.timingsMs))
</script>

<template>
  <section class="card" aria-label="미리보기 요약">
    <header>
      <h2>요약</h2>
      <span class="chip" :class="STATUS_CHIP[preview.status]">{{ PREVIEW_STATUS_LABEL[preview.status] }}</span>
      <a v-if="!consumed" class="btn btn-sm" :href="exportUrl(preview.previewId)" download>결과 JSON 내보내기</a>
    </header>

    <div class="card-body stack">
      <!-- 잔여 확인 실패: 롤백 뒤 남은 변경을 검사하다 실패했다(Q31) -->
      <div v-if="preview.residualCheckFailed" class="banner banner-err" role="alert">
        <div>
          <strong>잔여 확인 실패</strong>
          롤백 뒤 원본 기준 스냅샷과 비교해 남은 변경을 확인하려 했지만 끝내지 못했습니다. 남은 변경이 없다고 말할 수 없고, 샌드박스는 오염 상태로 표시됩니다.
          [적용]은 막히며 샌드박스를 새로 고쳐야 합니다.
          <span v-if="preview.residualCheckMessage" class="detail">사유: {{ preview.residualCheckMessage }}</span>
        </div>
      </div>

      <div v-if="STATUS_NOTE[preview.status]" class="banner" :class="preview.status === 'truncated' ? 'banner-warn' : 'banner-err'">
        <div>{{ STATUS_NOTE[preview.status] }}</div>
      </div>

      <dl class="kv">
        <dt>미리보기</dt>
        <dd class="mono">{{ preview.previewId }}</dd>
        <dt>SQL 지문</dt>
        <dd class="mono" :title="preview.sqlHash">{{ shortHash(preview.sqlHash) }}</dd>
        <dt>만든 시각</dt>
        <dd>{{ formatTime(preview.createdAt) }}</dd>
      </dl>

      <div>
        <h3>문장별 결과</h3>
        <table class="data">
          <thead>
            <tr>
              <th class="num">#</th>
              <th>문장</th>
              <th>상태</th>
              <th class="num">영향 행</th>
            </tr>
          </thead>
          <tbody>
            <template v-for="s in preview.statements" :key="s.index">
              <tr>
                <td class="num">{{ s.index }}</td>
                <td>{{ s.kind }} <span class="muted">{{ s.target }}</span></td>
                <td>
                  <span class="chip" :class="s.status === 'ok' ? 'chip-ok' : s.status === 'not_run' ? '' : 'chip-err'">
                    {{ STATEMENT_STATUS_LABEL[s.status] }}
                  </span>
                </td>
                <td class="num">{{ s.rowsAffected === null ? '-' : formatCount(s.rowsAffected) }}</td>
              </tr>
              <tr v-if="s.error">
                <td></td>
                <td colspan="3" class="err-row">
                  오류 {{ s.error.errorNumber }}<template v-if="s.error.procedure"> ({{ s.error.procedure }})</template>:
                  {{ s.error.message }}
                  <span v-if="s.error.isToolFault" class="chip chip-err">도구 오류</span>
                </td>
              </tr>
            </template>
          </tbody>
        </table>
      </div>

      <div>
        <h3>테이블별 건수(최종 차이)</h3>
        <p v-if="preview.netSummary.length === 0" class="muted none">바뀐 테이블이 없습니다.</p>
        <table v-else class="data">
          <thead>
            <tr>
              <th>테이블</th>
              <th class="num">추가</th>
              <th class="num">수정</th>
              <th class="num">삭제</th>
              <th v-if="hasMultiset" class="num">제거·추가(다중집합)</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="r in preview.netSummary" :key="r.table">
              <td>{{ r.table }}</td>
              <td class="num">{{ formatCount(r.inserted) }}</td>
              <td class="num">{{ formatCount(r.updated) }}</td>
              <td class="num">{{ formatCount(r.deleted) }}</td>
              <td v-if="hasMultiset" class="num">
                <template v-if="r.multisetRemoved > 0 || r.multisetAdded > 0">
                  제거 {{ formatCount(r.multisetRemoved) }} · 추가 {{ formatCount(r.multisetAdded) }}
                </template>
                <template v-else>-</template>
              </td>
            </tr>
          </tbody>
          <tfoot>
            <tr>
              <th>합계</th>
              <th class="num">{{ formatCount(totals.inserted) }}</th>
              <th class="num">{{ formatCount(totals.updated) }}</th>
              <th class="num">{{ formatCount(totals.deleted) }}</th>
              <th v-if="hasMultiset" class="num">제거 {{ formatCount(totals.removed) }} · 추가 {{ formatCount(totals.added) }}</th>
            </tr>
          </tfoot>
        </table>
      </div>

      <div>
        <h3>경고</h3>
        <DiagnosticList :items="preview.warnings" empty-text="경고 없음" />
      </div>

      <div>
        <h3>롤백 뒤 남은 변경</h3>
        <NetChangeList
          :items="preview.residualCommitted"
          :empty-text="preview.residualCheckFailed ? '확인하지 못했습니다(위 배너)' : '없음 — 트랜잭션 밖에서 확정된 변경이 없습니다'"
        />
        <p v-if="preview.residualCommitted.length > 0" class="muted none">
          트리거가 롤백 뒤에 남긴 변경입니다(예: CATCH 블록의 오류 로그 INSERT). 샌드박스는 오염으로 표시되고 다시 만들어집니다.
        </p>
      </div>

      <div>
        <h3>환경 지문</h3>
        <dl class="kv">
          <dt>SQL Server</dt>
          <dd>{{ fp.productVersion }} · {{ fp.edition }}</dd>
          <dt>호환 수준</dt>
          <dd>{{ fp.compatibilityLevel }}</dd>
          <dt>정렬 규칙</dt>
          <dd>{{ fp.collation }}</dd>
          <dt>트리거 설정</dt>
          <dd>
            중첩 {{ fp.nestedTriggers }} · 재귀 {{ fp.isRecursiveTriggersOn ? 'ON' : 'OFF' }} · 트리거 결과 반환 금지
            {{ fp.disallowResultsFromTriggers }}
          </dd>
          <dt>세션 옵션</dt>
          <dd>{{ fp.sessionOptions }}</dd>
          <dt>일치 여부</dt>
          <dd>
            <span
              v-if="sandbox.status"
              class="chip"
              :class="sandbox.status.fingerprintMatch ? 'chip-ok' : 'chip-err'"
            >
              원본과 {{ sandbox.status.fingerprintMatch ? '일치' : '불일치' }}
            </span>
            <span v-else class="muted">알 수 없음</span>
          </dd>
        </dl>
      </div>

      <div>
        <h3>수집 범위</h3>
        <dl class="kv">
          <dt>원본 → 샌드박스</dt>
          <dd>{{ preview.baseline.originDb }} → {{ preview.baseline.sandboxDb }}</dd>
          <dt>기준선 시각</dt>
          <dd>{{ formatTime(preview.baseline.backupFinishedAt) }}</dd>
          <dt>행 이미지</dt>
          <dd>
            {{ formatCount(preview.capture.imageCount) }} / 상한 {{ formatCount(preview.capture.cap) }}
            <span v-if="preview.capture.truncated" class="chip chip-warn">상한 초과</span>
          </dd>
          <dt>수집 매니페스트</dt>
          <dd>{{ preview.capture.manifestVersion }}</dd>
          <template v-if="manifest">
            <dt>대상 테이블</dt>
            <dd>
              수집 {{ manifest.coveredTables }}개 · 제외 {{ manifest.excludedTables }}개
              <template v-if="manifest.auditNotLast > 0"> · 감사 순서 불확실 {{ manifest.auditNotLast }}개</template>
            </dd>
          </template>
        </dl>
      </div>

      <details>
        <summary>단계별 소요 시간</summary>
        <dl class="kv">
          <template v-for="[k, v] in timings" :key="k">
            <dt>{{ k }}</dt>
            <dd>{{ formatMs(v) }}</dd>
          </template>
        </dl>
      </details>
    </div>
  </section>
</template>

<style scoped>
.stack {
  display: grid;
  gap: 12px;
}
.detail {
  display: block;
  margin-top: 2px;
}
.err-row {
  color: var(--err);
}
.none {
  margin: 4px 0 0;
  font-size: 12px;
}
summary {
  cursor: pointer;
  font-size: 12.5px;
  color: var(--fg-muted);
}
tfoot th {
  border-bottom: none;
}
</style>
