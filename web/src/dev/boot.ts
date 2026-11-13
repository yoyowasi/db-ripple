// 시험 데이터 모드에서 화면을 연 뒤 그 상태까지 자동으로 진행한다(SQL 넣기 → 분석 → 미리보기 → 필요하면 적용).
import { usePreviewStore } from '../stores/preview'
import { currentFixture } from './fakeApi'
import { FIXTURES } from './fixtures'

let booted = false

export async function bootFixture(): Promise<void> {
  const id = currentFixture()
  if (!id || booted) return
  booted = true
  const fx = FIXTURES[id]
  const store = usePreviewStore()
  store.imageCap = fx.imageCap
  store.setSql(fx.sql)
  await store.analyzeNow()
  if (!fx.autoRun) return
  await store.runPreview()
  if (fx.autoApply && store.preview) await store.applyNow()
}
