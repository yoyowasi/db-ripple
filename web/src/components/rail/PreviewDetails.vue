<script setup lang="ts">
// 레일 맨 아래 접이식 "자세히": 미리보기 id·SQL 지문·만든 시각, 환경 지문, 수집 범위, 단계별 소요, 결과 JSON 내보내기.
import { computed } from 'vue'
import { exportUrl } from '../../api/client'
import type { PreviewResult } from '../../api/types'
import { formatCount, formatMs, formatTime, shortHash } from '../../lib/format'
import { useSandboxStore } from '../../stores/sandbox'

const props = defineProps<{ preview: PreviewResult; consumed: boolean }>()
const sandbox = useSandboxStore()

const fp = computed(() => props.preview.baseline.fingerprint)
const manifest = computed(() => sandbox.status?.manifest ?? null)
const timings = computed(() => Object.entries(props.preview.timingsMs))
</script>

<template>
  <details class="more">
    <summary>자세히</summary>

    <h3>미리보기</h3>
    <dl class="kv">
      <dt>미리보기 id</dt>
      <dd class="mono">{{ preview.previewId }}</dd>
      <dt>SQL 지문</dt>
      <dd class="mono" :title="preview.sqlHash">{{ shortHash(preview.sqlHash) }}</dd>
      <dt>만든 시각</dt>
      <dd>{{ formatTime(preview.createdAt) }}</dd>
    </dl>
    <p v-if="!consumed" class="dl">
      <a class="btn btn-sm" :href="exportUrl(preview.previewId)" download>결과 JSON 내보내기</a>
    </p>

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
        <span v-if="sandbox.status" class="chip" :class="sandbox.status.fingerprintMatch ? 'chip-ok' : 'chip-err'">
          원본과 {{ sandbox.status.fingerprintMatch ? '일치' : '불일치' }}
        </span>
        <span v-else class="muted">알 수 없음</span>
      </dd>
    </dl>

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
      <dt>매니페스트</dt>
      <dd>{{ preview.capture.manifestVersion }}</dd>
      <template v-if="manifest">
        <dt>대상 테이블</dt>
        <dd>
          수집 {{ manifest.coveredTables }}개 · 제외 {{ manifest.excludedTables }}개
          <template v-if="manifest.auditNotLast > 0"> · 감사 순서 불확실 {{ manifest.auditNotLast }}개</template>
        </dd>
      </template>
    </dl>

    <h3>단계별 소요 시간</h3>
    <dl class="kv">
      <template v-for="[k, v] in timings" :key="k">
        <dt>{{ k }}</dt>
        <dd>{{ formatMs(v) }}</dd>
      </template>
    </dl>
  </details>
</template>

<style scoped>
.more > summary {
  cursor: pointer;
  color: var(--muted);
  font-weight: 600;
}
.more > summary:hover {
  color: var(--fg);
}
.more h3 {
  margin: 10px 0 4px;
}
.dl {
  margin-top: 6px;
}
</style>
