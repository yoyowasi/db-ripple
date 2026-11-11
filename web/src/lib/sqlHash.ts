// 서버 ContractFormulas.SqlHash 와 같은 식: "sha256:" + SHA-256(UTF-8, 정규화 없음) 소문자 16진.
// Web Crypto(crypto.subtle)는 보안 컨텍스트(https 또는 localhost)에서만 쓸 수 있다.
export async function sqlHash(sql: string): Promise<string> {
  const subtle = globalThis.crypto?.subtle
  if (!subtle) {
    throw new Error('이 브라우저 환경에서는 SHA-256을 계산할 수 없습니다(https 또는 localhost에서 여세요).')
  }
  const digest = await subtle.digest('SHA-256', new TextEncoder().encode(sql))
  const hex = Array.from(new Uint8Array(digest), (b) => b.toString(16).padStart(2, '0')).join('')
  return `sha256:${hex}`
}
