// 일정 간격으로 갱신되는 현재 시각(ms). 30분 만료 표시용. 컴포넌트가 사라지면 타이머를 지운다.
import { onBeforeUnmount, onMounted, ref } from 'vue'

export function useNow(intervalMs = 5000) {
  const now = ref(Date.now())
  let timer: number | undefined
  onMounted(() => {
    timer = window.setInterval(() => {
      now.value = Date.now()
    }, intervalMs)
  })
  onBeforeUnmount(() => {
    if (timer !== undefined) window.clearInterval(timer)
  })
  return now
}
