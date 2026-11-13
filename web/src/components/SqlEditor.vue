<script setup lang="ts">
// C1 쿼리 편집기. CodeMirror 6 을 Vue 래퍼 없이 onMounted 에서 직접 마운트한다.
import { autocompletion, completionKeymap } from '@codemirror/autocomplete'
import { defaultKeymap, history, historyKeymap } from '@codemirror/commands'
import { MSSQL, sql } from '@codemirror/lang-sql'
import { HighlightStyle, bracketMatching, indentOnInput, syntaxHighlighting } from '@codemirror/language'
import { lintGutter, linter, setDiagnostics } from '@codemirror/lint'
import { Compartment, EditorState } from '@codemirror/state'
import {
  EditorView,
  drawSelection,
  highlightActiveLine,
  highlightActiveLineGutter,
  keymap,
  lineNumbers,
} from '@codemirror/view'
import { tags } from '@lezer/highlight'
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { CatalogSchema, Diagnostic } from '../api/types'
import { schemaToCompletion, toCmDiagnostics } from '../lib/editorSupport'

const props = defineProps<{
  modelValue: string
  schema: CatalogSchema | null
  diagnostics: Diagnostic[]
  readonly?: boolean
}>()

const emit = defineEmits<{
  'update:modelValue': [value: string]
  /** Ctrl/⌘+Enter */
  run: []
}>()

const host = ref<HTMLDivElement | null>(null)
let view: EditorView | null = null
const sqlCompartment = new Compartment()
const editableCompartment = new Compartment()

// 색은 CSS 변수(밝은·어두운 모드 공통). 토큰 색은 styles/main.css 가 아니라 여기 theme 에서 정한다.
const highlight = HighlightStyle.define([
  { tag: tags.keyword, color: 'var(--cm-keyword, #1f5fd1)', fontWeight: '600' },
  { tag: [tags.string, tags.special(tags.string)], color: 'var(--cm-string, #0b7a4a)' },
  { tag: [tags.number, tags.bool, tags.null], color: 'var(--cm-number, #a85500)' },
  { tag: [tags.lineComment, tags.blockComment], color: 'var(--fg-muted)', fontStyle: 'italic' },
  { tag: [tags.typeName, tags.standard(tags.name)], color: 'var(--cm-type, #7a3fb5)' },
])

const theme = EditorView.theme({
  '&': { color: 'var(--fg)', backgroundColor: 'var(--panel)', height: '100%', fontSize: '13px' },
  '&.cm-focused': { outline: '2px solid var(--accent)', outlineOffset: '-2px' },
  // 합자를 끈다(JetBrains Mono의 <= != 등이 한 글자로 붙어 보이지 않게).
  '.cm-content': { fontFamily: 'var(--mono)', fontVariantLigatures: 'none', caretColor: 'var(--fg)', padding: '6px 0' },
  '.cm-scroller': { fontFamily: 'var(--mono)', fontVariantLigatures: 'none', lineHeight: '1.55' },
  '.cm-gutters': { backgroundColor: 'var(--sunk)', color: 'var(--code-cm)', borderRight: '1px solid var(--line)' },
  '.cm-activeLine': { backgroundColor: 'color-mix(in srgb, var(--accent) 7%, transparent)' },
  '.cm-activeLineGutter': { backgroundColor: 'color-mix(in srgb, var(--accent) 12%, transparent)' },
  '.cm-selectionBackground, &.cm-focused .cm-selectionBackground': {
    backgroundColor: 'var(--sel) !important',
  },
  '.cm-tooltip': {
    backgroundColor: 'var(--bg-elev)',
    color: 'var(--fg)',
    border: '1px solid var(--line)',
  },
  '.cm-tooltip-autocomplete ul li[aria-selected]': {
    backgroundColor: 'var(--accent)',
    color: 'var(--accent-fg)',
  },
  '.cm-diagnostic': { padding: '3px 6px 3px 8px' },
})

function sqlExtension(schema: CatalogSchema | null) {
  const c = schemaToCompletion(schema)
  return sql({
    dialect: MSSQL,
    schema: c.schema,
    defaultSchema: c.defaultSchema,
    upperCaseKeywords: true,
  })
}

onMounted(() => {
  if (!host.value) return
  view = new EditorView({
    parent: host.value,
    state: EditorState.create({
      doc: props.modelValue,
      extensions: [
        lineNumbers(),
        highlightActiveLineGutter(),
        history(),
        drawSelection(),
        indentOnInput(),
        bracketMatching(),
        highlightActiveLine(),
        syntaxHighlighting(highlight),
        autocompletion(),
        // 진단은 서버(POST /api/analyze)가 정한다. 여기서는 표시만 한다.
        linter(null),
        lintGutter(),
        // Tab 키를 가로채지 않는다(키보드 사용자가 편집기에 갇히지 않게).
        keymap.of([
          { key: 'Mod-Enter', run: () => (emit('run'), true) },
          ...completionKeymap,
          ...historyKeymap,
          ...defaultKeymap,
        ]),
        sqlCompartment.of(sqlExtension(props.schema)),
        editableCompartment.of(EditorView.editable.of(!props.readonly)),
        EditorView.contentAttributes.of({
          'aria-label': 'SQL 편집기. INSERT, UPDATE, DELETE 문을 입력하세요. Ctrl+Enter로 미리보기.',
        }),
        theme,
        EditorView.updateListener.of((u) => {
          if (u.docChanged) emit('update:modelValue', u.state.doc.toString())
        }),
      ],
    }),
  })
  applyDiagnostics()
})

onBeforeUnmount(() => {
  view?.destroy()
  view = null
})

function applyDiagnostics(): void {
  if (!view) return
  view.dispatch(setDiagnostics(view.state, toCmDiagnostics(props.diagnostics, view.state.doc)))
}

// 밖에서 SQL 이 바뀌면(예: 예시 넣기) 편집기에 반영한다.
watch(
  () => props.modelValue,
  (v) => {
    if (view && v !== view.state.doc.toString()) {
      view.dispatch({ changes: { from: 0, to: view.state.doc.length, insert: v } })
    }
  },
)

// 스키마가 바뀌면(baselineAt 변경) 자동완성만 갈아 끼운다.
watch(
  () => props.schema,
  (s) => view?.dispatch({ effects: sqlCompartment.reconfigure(sqlExtension(s)) }),
)

watch(() => props.readonly, (r) => view?.dispatch({ effects: editableCompartment.reconfigure(EditorView.editable.of(!r)) }))

watch(() => props.diagnostics, applyDiagnostics)
</script>

<template>
  <div ref="host" class="sql-editor"></div>
</template>

<style scoped>
.sql-editor {
  height: 100%;
  min-height: 0;
  overflow: hidden;
}
.sql-editor :deep(.cm-editor) {
  height: 100%;
}
</style>
