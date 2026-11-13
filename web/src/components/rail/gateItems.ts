// 적용 조건 목록(✓/✗). 판정은 applyGate.ts 가 하고, 이유도 조건별로 나눠 준다(GateResult.failures).
// 목록 밖 이유(이미 지워짐·이미 적용 시도·처리 중)는 ✗ 줄로 그대로 붙여 이유를 빠뜨리지 않는다.
import type { GateCondition, GateResult } from '../../lib/applyGate'

export interface GateItem {
  label: string
  ok: boolean
  /** 미충족 이유(applyGate 문구 그대로) */
  why: string[]
}

const CONDITIONS: { key: Exclude<GateCondition, 'other'>; label: string; onlyWhenFailing?: boolean }[] = [
  { key: 'preview', label: '미리보기 정상' },
  { key: 'fingerprint', label: '환경 지문 일치' },
  { key: 'window', label: '30분 안' },
  { key: 'sql', label: 'SQL이 미리보기 뒤 바뀌지 않음' },
  { key: 'residual', label: '잔여 변경 확인 성공' },
  { key: 'sandbox', label: '샌드박스 준비됨' },
  { key: 'lock', label: '다른 작업 잠금 없음' },
  // 원본 연결 조건은 막혔을 때만 줄로 나온다(옛 응답·정상일 때 목록은 그대로).
  { key: 'origin', label: '원본 연결 가능', onlyWhenFailing: true },
  // EXEC가 있으면 막힌 줄로만 나온다(미리보기 전용).
  { key: 'proc', label: '프로시저 호출 없음(EXEC는 미리보기 전용)', onlyWhenFailing: true },
]

export function gateItems(gate: GateResult, hasPreview: boolean): GateItem[] {
  // 미리보기가 없으면 다른 조건은 판단할 수 없다. 이유만 보인다.
  if (!hasPreview) return gate.reasons.map((r) => ({ label: r, ok: false, why: [] }))

  const items: GateItem[] = CONDITIONS.filter((c) => !c.onlyWhenFailing || gate.failures.some((f) => f.condition === c.key)).map((c) => {
    const why = gate.failures.filter((f) => f.condition === c.key).map((f) => f.text)
    const label =
      c.key === 'window' && gate.remainingMs !== null && gate.remainingMs > 0
        ? `30분 안(남은 시간 ${Math.max(1, Math.ceil(gate.remainingMs / 60000))}분)`
        : c.label
    return { label, ok: why.length === 0, why }
  })
  for (const f of gate.failures) if (f.condition === 'other') items.push({ label: f.text, ok: false, why: [] })
  return items
}
