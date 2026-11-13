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

/** 화면의 조건 목록 한 줄에 대응하는 키. 'other'는 목록 밖 이유(이미 지워짐·이미 적용 시도·처리 중 등). */
export type GateCondition = 'preview' | 'fingerprint' | 'window' | 'sql' | 'residual' | 'sandbox' | 'lock' | 'origin' | 'proc' | 'other'

export interface GateFailure {
  condition: GateCondition
  text: string
}

export interface GateResult {
  enabled: boolean
  /** 꺼진 이유 문장(failures의 text와 같은 순서) */
  reasons: string[]
  /** 꺼진 이유를 조건별로 나눈 것(화면이 문구를 추측하지 않게) */
  failures: GateFailure[]
  /** 만료까지 남은 ms. 미리보기가 없으면 null. */
  remainingMs: number | null
}

export function evaluateApplyGate(i: GateInput): GateResult {
  const failures: GateFailure[] = []
  const fail = (condition: GateCondition, text: string): void => {
    failures.push({ condition, text })
  }
  const result = (remainingMs: number | null): GateResult => ({
    enabled: failures.length === 0,
    reasons: failures.map((f) => f.text),
    failures,
    remainingMs,
  })
  const p = i.preview

  if (!p) {
    fail('preview', '미리보기가 없습니다. 먼저 [미리보기]를 실행하세요.')
    return result(null)
  }

  const created = new Date(p.createdAt).getTime()
  const remainingMs = Number.isNaN(created) ? null : created + APPLY_WINDOW_MS - i.now

  if (i.consumed) fail('other', '이 미리보기는 서버에서 이미 지워졌습니다(적용 또는 취소). 새 미리보기를 만드세요.')

  switch (p.status) {
    case 'ok':
      break
    case 'failed':
      fail('preview', '문장이 실패한 미리보기라 적용할 수 없습니다.')
      break
    case 'trigger_blocked':
      fail('preview', '트리거가 ROLLBACK 또는 거부한 문장이라 적용할 수 없습니다(E13). 원본에서도 같은 결과가 납니다.')
      break
    case 'truncated':
      fail('preview', '변경이 수집 상한을 넘어 행 이미지가 잘렸습니다. 검증할 수 없어 적용할 수 없습니다(D8).')
      break
  }

  // EXEC는 미리보기 전용(1차). 서버도 PROC_APPLY_NOT_SUPPORTED로 막지만 화면에서 미리 막는다.
  if (p.statements.some((s) => s.kind === 'EXEC')) fail('proc', '프로시저 호출은 아직 적용할 수 없습니다(미리보기 전용).')

  if (p.residualCheckFailed) {
    fail('residual', '롤백 뒤 남은 변경을 확인하지 못했습니다(잔여 확인 실패). 샌드박스가 오염 상태입니다.')
  }

  if (!i.sandbox) {
    fail('sandbox', '샌드박스 상태를 아직 모릅니다.')
  } else {
    if (!i.sandbox.fingerprintMatch) fail('fingerprint', '원본과 샌드박스의 환경 지문이 다릅니다.')
    if (i.sandbox.state === 'building') fail('sandbox', '샌드박스 기준선을 만드는 중입니다.')
    else if (i.sandbox.state === 'dirty') fail('sandbox', '샌드박스가 오염 상태입니다. 새로 고침이 필요합니다.')
    else if (i.sandbox.state === 'error') fail('sandbox', '샌드박스가 오류 상태입니다.')
    if (i.sandbox.locked) fail('lock', '다른 미리보기나 적용이 진행 중입니다(잠금).')
    // N4: 원본에 연결할 수 없으면 서버가 ORIGIN_UNREACHABLE로 막는다. 필드가 없으면(옛 응답·한 서버) 판정하지 않는다.
    if (i.sandbox.originReachable === false) fail('origin', '원본에 연결할 수 없어 적용할 수 없습니다(미리보기는 기존 기준선으로 가능).')
  }

  if (remainingMs !== null && remainingMs <= 0) fail('window', '미리보기가 만료됐습니다(30분). 새 미리보기를 만드세요.')

  if (i.editorSql !== i.previewSql) fail('sql', '편집기의 SQL이 미리보기 뒤에 바뀌었습니다. 같은 SQL로 미리보기를 다시 실행하세요.')

  const r = i.applyResult
  if (r && (r.status === 'committed' || r.status === 'drift_detected' || r.status === 'rolled_back_mismatch')) {
    fail('other', '이미 적용을 시도했습니다. 결과를 확인하고 새 미리보기를 만드세요.')
  }

  if (i.busy) fail('other', '요청을 처리하는 중입니다.')

  return result(remainingMs)
}
