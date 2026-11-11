// C1 편집기 보조: 스키마 → 자동완성, 서버 Diagnostic → CodeMirror 줄 표시.
import type { Completion } from '@codemirror/autocomplete'
import type { SQLConfig } from '@codemirror/lang-sql'
import type { Diagnostic as CmDiagnostic } from '@codemirror/lint'
import type { Text } from '@codemirror/state'
import type { CatalogSchema, Diagnostic } from '../api/types'

type SqlSchema = NonNullable<SQLConfig['schema']>

export interface SchemaCompletion {
  schema: SqlSchema
  defaultSchema: string | undefined
}

/**
 * 스키마 → { 스키마이름: { 테이블: [열…] } }. "Sales." 뒤에서 테이블, "Sales.SalesOrderDetail." 뒤에서 열이 나온다.
 * 열 설명에는 sqlType과 IDENTITY·계산 열·비결정 표시를 붙인다.
 */
export function schemaToCompletion(schema: CatalogSchema | null): SchemaCompletion {
  const root: Record<string, Record<string, Completion[]>> = {}
  if (schema) {
    for (const t of schema.tables) {
      const cols: Completion[] = t.columns.map((c) => {
        const tags: string[] = [c.sqlType]
        if (!c.isNullable) tags.push('NOT NULL')
        if (c.isIdentity) tags.push('IDENTITY')
        if (c.isComputed) tags.push('계산 열')
        if (c.volatile) tags.push('예상값')
        return { label: c.name, type: 'property', detail: tags.join(' ') }
      })
      const bySchema = (root[t.schema] ??= {})
      bySchema[t.name] = cols
    }
  }
  return {
    schema: root as unknown as SqlSchema,
    defaultSchema: root['dbo'] ? 'dbo' : undefined,
  }
}

function clamp(n: number, lo: number, hi: number): number {
  return Math.min(Math.max(n, lo), hi)
}

/** 서버 span은 1부터 시작하는 줄·열이고, 끝 열은 마지막 글자를 포함한다(Analysis SqlSyntax.Span). CodeMirror의 to는 배타적이라 끝 열을 그대로 더한다. */
export function toCmDiagnostics(diags: Diagnostic[], doc: Text): CmDiagnostic[] {
  return diags.map((d) => {
    const s = d.span
    const l1 = doc.line(clamp(s.startLine, 1, doc.lines))
    const l2 = doc.line(clamp(s.endLine, 1, doc.lines))
    const from = Math.min(l1.from + Math.max(s.startColumn - 1, 0), l1.to)
    let to = Math.min(l2.from + Math.max(s.endColumn, 0), l2.to)
    if (to <= from) to = Math.min(from + 1, doc.length)
    return {
      from: Math.min(from, to),
      to,
      severity: d.severity,
      message: `${d.message} [${d.ruleId}]`,
      source: d.ruleId,
    }
  })
}
