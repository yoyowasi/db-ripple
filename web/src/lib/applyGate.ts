// [적용] 활성 조건(week3-plan.md §1 web-1 C4, D8, E13).
// 켜지는 조건: status ok, fingerprintMatch, 만든 지 30분 안, 편집기 SQL이 미리보기 때와 같음.
// 꺼지면 이유를 모두 보인다. 서버의 선행 조건(ApplyPrecondition)이 마지막 판정이다.
import type { ApplyResult, PreviewResult, SandboxStatus } from '../api/types'

export const APPLY_WINDOW_MS = 30 * 60 * 1000

export interface GateInput {
  preview: PreviewResult | null
  sandbox: SandboxStatus | null
  editorSql: string
  previewSql: string
  now: number
  applyResult: ApplyResult | null
  /** 이 미리보기가 서버에서 이미 지워졌다(커밋·취소·404). */
  consumed: boolean
  busy: boolean
}

export interface GateResult {
  enabled: boolean
  reasons: string[]
  /** 만료까지 남은 ms. 미리보기가 없으면 null. */
  remainingMs: number | null
}

export function evaluateApplyGate(i: GateInput): GateResult {
  const reasons: string[] = []
  const p = i.preview

  if (!p) {
    return { enabled: false, reasons: ['미리보기가 없습니다. 먼저 [미리보기]를 실행하세요.'], remainingMs: null }
  }

  const created = new Date(p.createdAt).getTime()
  const remainingMs = Number.isNaN(created) ? null : created + APPLY_WINDOW_MS - i.now

  if (i.consumed) reasons.push('이 미리보기는 서버에서 이미 지워졌습니다(적용 또는 취소). 새 미리보기를 만드세요.')

  switch (p.status) {
    case 'ok':
      break
    case 'failed':
      reasons.push('문장이 실패한 미리보기라 적용할 수 없습니다.')
      break
    case 'trigger_blocked':
      reasons.push('트리거가 ROLLBACK 또는 거부한 문장이라 적용할 수 없습니다(E13). 원본에서도 같은 결과가 납니다.')
      break
    case 'truncated':
      reasons.push('변경이 수집 상한을 넘어 행 이미지가 잘렸습니다. 검증할 수 없어 적용할 수 없습니다(D8).')
      break
  }

  if (p.residualCheckFailed) {
    reasons.push('롤백 뒤 남은 변경을 확인하지 못했습니다(잔여 확인 실패). 샌드박스가 오염 상태입니다.')
  }

  if (!i.sandbox) {
    reasons.push('샌드박스 상태를 아직 모릅니다.')
  } else {
    if (!i.sandbox.fingerprintMatch) reasons.push('원본과 샌드박스의 환경 지문이 다릅니다.')
    if (i.sandbox.state === 'building') reasons.push('샌드박스 기준선을 만드는 중입니다.')
    else if (i.sandbox.state === 'dirty') reasons.push('샌드박스가 오염 상태입니다. 새로 고침이 필요합니다.')
    else if (i.sandbox.state === 'error') reasons.push('샌드박스가 오류 상태입니다.')
    if (i.sandbox.locked) reasons.push('다른 미리보기나 적용이 진행 중입니다(잠금).')
  }

  if (remainingMs !== null && remainingMs <= 0) reasons.push('미리보기가 만료됐습니다(30분). 새 미리보기를 만드세요.')

  if (i.editorSql !== i.previewSql) reasons.push('편집기의 SQL이 미리보기 뒤에 바뀌었습니다. 같은 SQL로 미리보기를 다시 실행하세요.')

  const r = i.applyResult
  if (r && (r.status === 'committed' || r.status === 'drift_detected' || r.status === 'rolled_back_mismatch')) {
    reasons.push('이미 적용을 시도했습니다. 결과를 확인하고 새 미리보기를 만드세요.')
  }

  if (i.busy) reasons.push('요청을 처리하는 중입니다.')

  return { enabled: reasons.length === 0, reasons, remainingMs }
}
