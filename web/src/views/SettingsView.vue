<script setup lang="ts">
// 설정 화면(N4): 모드 · 원본 · 샌드박스 · 백업 가져오기. [연결 시험]은 저장하지 않고 확인만 하고, [저장]은 프로필에 쓴다.
// 비밀번호는 입력 칸에만 있고(콘솔·localStorage·URL에 남기지 않는다) 저장 요청이 끝나면 바로 비운다.
import { computed, defineAsyncComponent, onMounted, reactive, ref, shallowRef } from 'vue'
import { RouterLink } from 'vue-router'
import { ApiError, describeApiError, getSettings, saveSettings, testSettings } from '../api/client'
import type {
  AuthKind,
  ConnectionMode,
  SettingsEndpointInput,
  SettingsRequest,
  SettingsTestResult,
  SettingsView,
} from '../api/types'
import FieldRow from '../components/settings/FieldRow.vue'
import ProblemBanner from '../components/ProblemBanner.vue'
import { useSandboxStore } from '../stores/sandbox'

// 개발 서버에서만: 시험 데이터 근거 표시(운영 빌드에서는 빠진다).
const FixtureNote = import.meta.env.DEV ? defineAsyncComponent(() => import('../dev/FixtureNote.vue')) : null

const sandbox = useSandboxStore()

const view = shallowRef<SettingsView | null>(null)
const loadError = shallowRef<ApiError | null>(null)
const loading = ref(true)

const form = reactive({
  mode: 'split' as ConnectionMode,
  origin: { server: '', database: '', auth: 'windows' as AuthKind, user: '', password: '', remember: false, trust: false, stored: false },
  sandbox: { server: '127.0.0.1,14333', database: '', auth: 'sql' as AuthKind, user: 'sa', password: '', remember: true, stored: false },
  backup: { serverPath: '', sharePath: '', importFile: '' },
})

const isEnv = computed(() => view.value?.source === 'env')
const firstRun = computed(() => !!view.value && !view.value.configured)
const split = computed(() => form.mode === 'split')

function fill(v: SettingsView): void {
  form.mode = v.mode ?? 'split'
  if (v.origin) {
    Object.assign(form.origin, {
      server: v.origin.server ?? '',
      database: v.origin.database ?? '',
      auth: v.origin.auth ?? 'windows',
      user: v.origin.user ?? '',
      remember: false,
      trust: !!v.origin.trustServerCertificate,
      stored: !!v.origin.hasStoredPassword,
    })
  }
  if (v.sandbox) {
    Object.assign(form.sandbox, {
      server: v.sandbox.server ?? '',
      database: v.sandbox.database ?? '',
      auth: v.sandbox.auth ?? 'sql',
      user: v.sandbox.user ?? '',
      remember: !!v.sandbox.hasStoredPassword || v.configured === false,
      stored: !!v.sandbox.hasStoredPassword,
    })
  }
  if (v.backup) {
    form.backup.serverPath = v.backup.serverPath ?? ''
    form.backup.sharePath = v.backup.sharePath ?? ''
    form.backup.importFile = v.backup.importFile ?? ''
  }
  form.origin.password = ''
  form.sandbox.password = ''
}

async function load(): Promise<void> {
  loading.value = true
  loadError.value = null
  try {
    const v = await getSettings()
    view.value = v
    fill(v)
  } catch (e) {
    loadError.value = e instanceof ApiError ? e : null
  } finally {
    loading.value = false
  }
}
onMounted(load)

// ---- 요청 만들기 ----

const blank = (s: string): string | null => (s.trim() === '' ? null : s.trim())

function endpoint(e: typeof form.origin | typeof form.sandbox): SettingsEndpointInput {
  const sql = e.auth === 'sql'
  const input: SettingsEndpointInput = {
    server: e.server.trim(),
    database: e.database.trim(),
    auth: e.auth,
    user: sql ? blank(e.user) : null,
    rememberPassword: sql && e.remember,
  }
  if (sql && e.password !== '') input.password = e.password // 바꿀 때만 보낸다
  return input
}

function toRequest(): SettingsRequest {
  const o = endpoint(form.origin)
  // 한 서버: 연결은 샌드박스 절 하나로 만들고 원본 절은 DB 이름만 쓴다(서버·인증은 무시).
  const origin = split.value
    ? { ...o, trustServerCertificate: form.origin.trust }
    : { server: '', database: form.origin.database.trim(), auth: 'windows' as AuthKind, user: null, rememberPassword: false, trustServerCertificate: false }
  return {
    mode: form.mode,
    origin,
    sandbox: endpoint(form.sandbox),
    backup: { serverPath: blank(form.backup.serverPath), sharePath: blank(form.backup.sharePath), importFile: blank(form.backup.importFile) },
  }
}

// ---- 오류 ----

/** 400 errors 키를 소문자로 맞춘다("origin.server"). */
const errors = shallowRef<Record<string, string>>({})
const generalError = shallowRef<string | null>(null)
const err = (key: string): string | undefined => errors.value[key.toLowerCase()]

const FIELD_KEYS = new Set([
  'mode',
  'origin.server',
  'origin.database',
  'origin.user',
  'origin.password',
  'sandbox.server',
  'sandbox.database',
  'sandbox.user',
  'sandbox.password',
  'backup.serverpath',
  'backup.sharepath',
  'backup.importfile',
])
/** 칸에 붙지 않은 오류(키를 모르는 것)는 맨 위에 모아 보인다. */
const looseErrors = computed(() =>
  Object.entries(errors.value)
    .filter(([k]) => !FIELD_KEYS.has(k))
    .map(([k, m]) => `${k}: ${m}`),
)

function takeErrors(e: unknown): void {
  errors.value = {}
  generalError.value = null
  if (e instanceof ApiError && e.status === 400 && e.problem?.errors) {
    errors.value = Object.fromEntries(Object.entries(e.problem.errors).map(([k, m]) => [k.toLowerCase(), m.join(' ')]))
    generalError.value = '입력을 확인하세요. 칸마다 이유가 적혀 있습니다.'
  } else if (e instanceof ApiError) {
    generalError.value = describeApiError(e)
  } else {
    generalError.value = '화면 처리 중 오류가 났습니다.'
  }
}

// ---- 연결 시험 · 저장 ----

const testing = ref(false)
const saving = ref(false)
const testResult = shallowRef<SettingsTestResult | null>(null)
const saved = ref(false)
const busy = computed(() => testing.value || saving.value)

async function runTest(): Promise<void> {
  if (busy.value || isEnv.value) return
  testing.value = true
  saved.value = false
  testResult.value = null
  errors.value = {}
  generalError.value = null
  try {
    testResult.value = await testSettings(toRequest())
  } catch (e) {
    takeErrors(e)
  } finally {
    testing.value = false
  }
}

async function save(): Promise<void> {
  if (busy.value || isEnv.value) return
  saving.value = true
  saved.value = false
  errors.value = {}
  generalError.value = null
  try {
    await saveSettings(toRequest())
    saved.value = true
    testResult.value = null
  } catch (e) {
    takeErrors(e)
  } finally {
    // 요청이 끝났으니 비밀번호 입력값은 바로 비운다(오류가 나면 다시 입력해야 한다).
    form.origin.password = ''
    form.sandbox.password = ''
    saving.value = false
  }
  if (saved.value) {
    await load()
    void sandbox.poll()
  }
}

const modeText = computed(() =>
  split.value
    ? '원본은 개발 서버, 샌드박스는 이 PC의 SQL Server(예: Docker)입니다. [적용]만 원본에 씁니다.'
    : '원본과 샌드박스가 한 SQL Server에 있습니다. 서버 연결은 아래 "샌드박스" 절 하나로 만들고, 원본 DB는 이름만 적습니다.',
)

const pwHint = (stored: boolean, auth: AuthKind): string | undefined =>
  auth !== 'sql'
    ? undefined
    : stored
      ? '서버가 저장된 비밀번호를 알고 있습니다. 바꿀 때만 입력하세요. 서버나 사용자 이름을 바꾸면 다시 입력해야 합니다.'
      : '비밀번호를 입력하세요. 저장 요청이 끝나면 이 칸은 비워집니다.'
</script>

<template>
  <div class="settings">
    <h1 class="ttl">설정</h1>

    <ProblemBanner v-if="sandbox.noSession" :error="sandbox.noSession" title="이 창에서는 쓸 수 없습니다." />
    <ProblemBanner v-else-if="loadError" :error="loadError" title="설정을 읽지 못했습니다." />
    <p v-else-if="loading" class="hint" role="status">설정을 읽는 중…</p>

    <template v-if="view">
      <div v-if="firstRun" class="banner banner-info" role="status">
        <svg class="ic" viewBox="0 0 16 16" aria-hidden="true"><circle cx="8" cy="8" r="5.8" /><path d="M8 7.4v3.6M8 5v.1" /></svg>
        <div>
          <strong>처음 실행입니다</strong>
          연결 정보를 입력하고 [연결 시험]으로 확인한 뒤 [저장]하세요. 저장하면 홈으로 이동해 샌드박스 기준선을 만들 수 있습니다.
        </div>
      </div>

      <div v-if="isEnv" class="banner banner-warn" role="status">
        <svg class="ic" viewBox="0 0 16 16" aria-hidden="true"><path d="M8 2.2L14.2 13H1.8z" /><path d="M8 6.6v3M8 11.4v.1" /></svg>
        <div>
          <strong>환경 변수로 설정됨</strong>
          DBRIPPLE_*_CONNECTION 환경 변수가 있어 그 값을 씁니다. 이 화면에서는 읽기만 되고 바꿀 수 없습니다. 바꾸려면 환경 변수를 지우고 실행 파일을 다시 여세요.
        </div>
      </div>

      <div class="banner banner-warn" role="note">
        <svg class="ic" viewBox="0 0 16 16" aria-hidden="true"><path d="M8 2.2L14.2 13H1.8z" /><path d="M8 6.6v3M8 11.4v.1" /></svg>
        <div>
          <strong>개발 DB에 개인정보가 들어 있을 수 있습니다</strong>
          샌드박스를 만들 때 원본 DB를 백업해 이 PC의 SQL Server에 복원합니다. 이 PC로 가져온 백업 파일은 복원이 끝나면 지웁니다. 원본 서버 쪽에 만들어진
          <code>dbr_*.bak</code> 파일은 자동으로 지워지지 않으니 직접 지워야 합니다.
        </div>
      </div>

      <div v-if="generalError" class="banner banner-err" role="alert">
        <svg class="ic" viewBox="0 0 16 16" aria-hidden="true"><circle cx="8" cy="8" r="5.8" /><path d="M4 12L12 4" /></svg>
        <div>
          {{ generalError }}
          <ul v-if="looseErrors.length" class="loose">
            <li v-for="m in looseErrors" :key="m">{{ m }}</li>
          </ul>
        </div>
      </div>

      <form class="form" novalidate autocomplete="off" @submit.prevent="save">
        <fieldset class="card" :disabled="isEnv">
          <legend class="sr-only">연결 모드</legend>
          <header><h2>연결 모드</h2></header>
          <div class="card-body grid">
            <div class="radios" role="radiogroup" aria-label="연결 모드" :aria-describedby="err('mode') ? 'mode-err' : 'mode-d'">
              <label><input v-model="form.mode" type="radio" name="mode" value="split" /> 분리 <span class="sub">원본 서버 + 내 PC 샌드박스</span></label>
              <label><input v-model="form.mode" type="radio" name="mode" value="single_server" /> 한 서버</label>
            </div>
            <p id="mode-d" class="hint">{{ modeText }}</p>
            <p v-if="err('mode')" id="mode-err" class="err" role="alert">{{ err('mode') }}</p>
          </div>
        </fieldset>

        <fieldset class="card" :disabled="isEnv">
          <legend class="sr-only">원본</legend>
          <header><h2>원본</h2><span class="sub">{{ split ? '[적용]이 쓰는 개발 서버' : '원본 DB 이름만 쓰는 절' }}</span></header>
          <div class="card-body grid">
            <template v-if="split">
              <FieldRow id="o-server" label="서버" :error="err('origin.server')" hint="예: devsql01 또는 devsql01,1433">
                <template #default="{ describedby, invalid }">
                  <input id="o-server" v-model="form.origin.server" type="text" class="field" spellcheck="false" :aria-describedby="describedby" :aria-invalid="invalid" />
                </template>
              </FieldRow>
            </template>
            <FieldRow id="o-db" label="DB 이름" :error="err('origin.database')">
              <template #default="{ describedby, invalid }">
                <input id="o-db" v-model="form.origin.database" type="text" class="field" spellcheck="false" :aria-describedby="describedby" :aria-invalid="invalid" />
              </template>
            </FieldRow>
            <template v-if="split">
              <div class="row-radio" role="radiogroup" aria-label="원본 인증">
                <span class="lbl" aria-hidden="true">인증</span>
                <div class="radios">
                  <label><input v-model="form.origin.auth" type="radio" name="o-auth" value="windows" /> Windows 인증</label>
                  <label><input v-model="form.origin.auth" type="radio" name="o-auth" value="sql" /> SQL Server 인증</label>
                </div>
              </div>
              <template v-if="form.origin.auth === 'sql'">
                <FieldRow id="o-user" label="사용자" :error="err('origin.user')">
                  <template #default="{ describedby, invalid }">
                    <input id="o-user" v-model="form.origin.user" type="text" class="field" spellcheck="false" autocomplete="off" :aria-describedby="describedby" :aria-invalid="invalid" />
                  </template>
                </FieldRow>
                <FieldRow id="o-pw" label="비밀번호" :error="err('origin.password')" :hint="pwHint(form.origin.stored, 'sql')">
                  <template #default="{ describedby, invalid }">
                    <input
                      id="o-pw"
                      v-model="form.origin.password"
                      type="password"
                      class="field"
                      autocomplete="new-password"
                      :placeholder="form.origin.stored ? '저장된 비밀번호 사용 중' : ''"
                      :aria-describedby="describedby"
                      :aria-invalid="invalid"
                    />
                  </template>
                </FieldRow>
                <label class="chk"><input v-model="form.origin.remember" type="checkbox" /> 비밀번호 기억 <span class="sub">이 PC의 사용자 계정으로 암호화해 저장합니다. 끄면 실행 파일을 닫을 때까지만 기억합니다.</span></label>
              </template>
              <div class="trust">
                <label class="chk"><input v-model="form.origin.trust" type="checkbox" aria-describedby="trust-warn" /> 서버 인증서 검증 안 함 <span class="sub">개발 서버만</span></label>
                <p v-if="form.origin.trust" id="trust-warn" class="trust-warn" role="note">
                  서버 인증서를 검증하지 않습니다. 연결 상대가 진짜 서버인지 확인하지 못하니 신뢰하는 개발 서버에서만 켜세요.
                </p>
                <p v-else id="trust-warn" class="sr-only">끄면 서버 인증서를 검증합니다.</p>
              </div>
            </template>
          </div>
        </fieldset>

        <fieldset class="card" :disabled="isEnv">
          <legend class="sr-only">샌드박스</legend>
          <header><h2>샌드박스</h2><span class="sub">{{ split ? '미리보기를 실행하는 이 PC의 SQL Server' : '원본과 같은 서버 연결' }}</span></header>
          <div class="card-body grid">
            <FieldRow
              id="s-server"
              label="서버"
              :error="err('sandbox.server')"
              :hint="split ? '이 PC의 SQL Server만 쓸 수 있습니다(127.0.0.1, localhost 등). 기본 127.0.0.1,14333' : undefined"
            >
              <template #default="{ describedby, invalid }">
                <input id="s-server" v-model="form.sandbox.server" type="text" class="field" spellcheck="false" :aria-describedby="describedby" :aria-invalid="invalid" />
              </template>
            </FieldRow>
            <FieldRow
              id="s-db"
              label="DB 이름"
              :error="err('sandbox.database')"
              :hint="split ? undefined : '원본 DB 이름과 달라야 합니다.'"
            >
              <template #default="{ describedby, invalid }">
                <input id="s-db" v-model="form.sandbox.database" type="text" class="field" spellcheck="false" :aria-describedby="describedby" :aria-invalid="invalid" />
              </template>
            </FieldRow>
            <div class="row-radio" role="radiogroup" aria-label="샌드박스 인증">
              <span class="lbl" aria-hidden="true">인증</span>
              <div class="radios">
                <label><input v-model="form.sandbox.auth" type="radio" name="s-auth" value="windows" /> Windows 인증</label>
                <label><input v-model="form.sandbox.auth" type="radio" name="s-auth" value="sql" /> SQL Server 인증</label>
              </div>
            </div>
            <template v-if="form.sandbox.auth === 'sql'">
              <FieldRow id="s-user" label="사용자" :error="err('sandbox.user')">
                <template #default="{ describedby, invalid }">
                  <input id="s-user" v-model="form.sandbox.user" type="text" class="field" spellcheck="false" autocomplete="off" :aria-describedby="describedby" :aria-invalid="invalid" />
                </template>
              </FieldRow>
              <FieldRow id="s-pw" label="비밀번호" :error="err('sandbox.password')" :hint="pwHint(form.sandbox.stored, 'sql')">
                <template #default="{ describedby, invalid }">
                  <input
                    id="s-pw"
                    v-model="form.sandbox.password"
                    type="password"
                    class="field"
                    autocomplete="new-password"
                    :placeholder="form.sandbox.stored ? '저장된 비밀번호 사용 중' : ''"
                    :aria-describedby="describedby"
                    :aria-invalid="invalid"
                  />
                </template>
              </FieldRow>
              <label class="chk"><input v-model="form.sandbox.remember" type="checkbox" /> 비밀번호 기억 <span class="sub">이 PC의 사용자 계정으로 암호화해 저장합니다.</span></label>
            </template>
          </div>
        </fieldset>

        <fieldset class="card" :disabled="isEnv">
          <legend class="sr-only">백업 가져오기</legend>
          <header><h2>백업 가져오기</h2><span class="sub">원본 DB를 샌드박스로 옮기는 방법</span></header>
          <div class="card-body grid">
            <p class="hint lead">
              샌드박스 기준선은 원본 DB의 백업(.bak)을 복원해 만듭니다. 아래 중 한 가지 방식을 씁니다.
              <b>서버 쪽 폴더 + 공유 폴더</b>는 원본 서버가 .bak을 쓰고 이 PC가 공유 경로로 읽는 방식이고,
              <b>.bak 파일 직접 지정</b>은 이미 가진 백업 파일을 복원하는 방식입니다(원본에서 백업하지 않습니다).
            </p>
            <FieldRow id="b-server" label="서버 쪽 백업 폴더" :error="err('backup.serverPath')" hint="원본 SQL Server가 .bak을 쓰는 폴더(서버 기준 경로). 예: D:\Backup">
              <template #default="{ describedby, invalid }">
                <input id="b-server" v-model="form.backup.serverPath" type="text" class="field" spellcheck="false" :aria-describedby="describedby" :aria-invalid="invalid" />
              </template>
            </FieldRow>
            <FieldRow id="b-share" label="공유 폴더(UNC)" :error="err('backup.sharePath')" hint="위 폴더를 이 PC에서 읽는 공유 경로. 예: \\devsql01\dbr">
              <template #default="{ describedby, invalid }">
                <input id="b-share" v-model="form.backup.sharePath" type="text" class="field" spellcheck="false" :aria-describedby="describedby" :aria-invalid="invalid" />
              </template>
            </FieldRow>
            <FieldRow id="b-file" label=".bak 파일 직접 지정" :error="err('backup.importFile')" hint="이 PC에 있는 .bak 파일의 전체 경로. 지정하면 원본에서 백업하지 않습니다.">
              <template #default="{ describedby, invalid }">
                <input id="b-file" v-model="form.backup.importFile" type="text" class="field" spellcheck="false" :aria-describedby="describedby" :aria-invalid="invalid" />
              </template>
            </FieldRow>
          </div>
        </fieldset>

        <div v-if="!isEnv" class="actions">
          <button type="button" class="btn" :aria-disabled="busy" @click="runTest">{{ testing ? '시험하는 중…' : '연결 시험' }}</button>
          <button type="submit" class="btn btn-primary" :aria-disabled="busy" @click.prevent="save">{{ saving ? '저장하는 중…' : '저장' }}</button>
          <RouterLink to="/" class="back">홈으로</RouterLink>
        </div>
      </form>

      <div class="results" aria-live="polite">
        <div v-if="saved" class="banner banner-ok" role="status">
          <svg class="ic" viewBox="0 0 16 16" aria-hidden="true"><path d="M3 8.5l3.2 3.2L13 4.8" /></svg>
          <div>
            <strong>저장했습니다</strong>
            연결을 새로 만들었으므로 샌드박스 기준선을 다시 만들어야 합니다. 홈의 [샌드박스 새로 고침]을 누르세요(원본 DB를 백업해 복원하므로 시간이 걸립니다).
            <RouterLink to="/" class="go">홈으로 이동</RouterLink>
          </div>
        </div>

        <section v-if="testResult" class="card" aria-labelledby="t-h">
          <header><h2 id="t-h">연결 시험 결과</h2></header>
          <div class="card-body tests">
            <div v-if="testResult.origin" class="tcol">
              <h3>원본 <span class="chip" :class="testResult.origin.ok ? 'chip-ok' : 'chip-err'">{{ testResult.origin.ok ? '연결됨' : '실패' }}</span></h3>
              <p v-if="testResult.origin.error" class="err" role="alert">{{ testResult.origin.error }}</p>
              <dl v-if="testResult.origin.ok" class="kv">
                <dt>서버 이름</dt><dd>{{ testResult.origin.serverName ?? '-' }}</dd>
                <dt>버전</dt><dd>{{ testResult.origin.productVersion ?? '-' }}<template v-if="testResult.origin.edition"> · {{ testResult.origin.edition }}</template></dd>
                <dt>sysadmin</dt>
                <dd>{{ testResult.origin.isSysadmin === null || testResult.origin.isSysadmin === undefined ? '모름' : testResult.origin.isSysadmin ? '예(권한이 넓습니다)' : '아니오' }}</dd>
                <dt>백업 권한</dt>
                <dd>{{ testResult.origin.canBackup === null || testResult.origin.canBackup === undefined ? '모름' : testResult.origin.canBackup ? '있음' : '없음' }}</dd>
                <dt>스냅샷(CREATE DATABASE) 권한</dt>
                <dd>
                  {{ testResult.origin.canCreateDatabase === null || testResult.origin.canCreateDatabase === undefined ? '모름' : testResult.origin.canCreateDatabase ? '있음' : '없음' }}
                  <span v-if="testResult.origin.canCreateDatabase === false" class="sub">없으면 [적용]이 약한 검증(KEYSET)으로 동의를 묻습니다.</span>
                </dd>
              </dl>
            </div>
            <div v-if="testResult.sandbox" class="tcol">
              <h3>샌드박스 <span class="chip" :class="testResult.sandbox.ok ? 'chip-ok' : 'chip-err'">{{ testResult.sandbox.ok ? '연결됨' : '실패' }}</span></h3>
              <p v-if="testResult.sandbox.error" class="err" role="alert">{{ testResult.sandbox.error }}</p>
              <dl v-if="testResult.sandbox.ok" class="kv">
                <dt>서버 이름</dt><dd>{{ testResult.sandbox.serverName ?? '-' }}</dd>
                <dt>버전</dt><dd>{{ testResult.sandbox.productVersion ?? '-' }}</dd>
                <dt>이 PC(루프백)</dt>
                <dd>{{ testResult.sandbox.isLoopback === null || testResult.sandbox.isLoopback === undefined ? '모름' : testResult.sandbox.isLoopback ? '예' : '아니오' }}</dd>
              </dl>
            </div>
          </div>
          <ul v-if="testResult.warnings?.length" class="warns">
            <li v-for="w in testResult.warnings" :key="w" class="banner banner-warn">
              <svg class="ic" viewBox="0 0 16 16" aria-hidden="true"><path d="M8 2.2L14.2 13H1.8z" /><path d="M8 6.6v3M8 11.4v.1" /></svg>
              <span>{{ w }}</span>
            </li>
          </ul>
        </section>
      </div>
    </template>
    <component :is="FixtureNote" v-if="FixtureNote" />
  </div>
</template>

<style scoped>
.settings {
  display: grid;
  gap: 12px;
  max-width: 780px;
}
.ttl {
  font-size: 16px;
  font-weight: 600;
}
.form {
  display: grid;
  gap: 12px;
}
fieldset.card {
  margin: 0;
  padding: 0;
}
fieldset.card > header {
  display: flex;
  align-items: baseline;
  gap: 10px;
}
fieldset.card > header h2 {
  margin-right: 0;
}
.sub {
  font-size: 12px;
  font-weight: 400;
  color: var(--muted);
}
.grid {
  display: grid;
  gap: 10px;
}
.radios {
  display: flex;
  flex-wrap: wrap;
  gap: 6px 18px;
}
.radios label,
.chk {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  cursor: pointer;
}
.row-radio {
  display: grid;
  grid-template-columns: 150px minmax(0, 1fr);
  gap: 4px 12px;
  align-items: center;
}
.lbl {
  font-weight: 500;
}
.chk {
  flex-wrap: wrap;
  margin-left: 162px;
}
.trust {
  display: grid;
  gap: 4px;
}
.trust-warn {
  margin-left: 162px;
  padding: 5px 8px;
  border: 1px solid var(--warn);
  border-radius: var(--r);
  background: var(--warn-bg);
  font-size: 12px;
}
.hint {
  color: var(--muted);
  font-size: 12px;
}
.hint.lead {
  font-size: 13px;
}
.err {
  color: var(--err);
  font-weight: 600;
  font-size: 12px;
}
.loose {
  margin: 4px 0 0;
  padding-left: 18px;
}
.actions {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  align-items: center;
}
.back {
  margin-left: 6px;
}
.results {
  display: grid;
  gap: 12px;
}
.go {
  margin-left: 8px;
}
.tests {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(280px, 1fr));
  gap: 14px;
}
.tcol h3 {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 6px;
  font-size: 13px;
  color: var(--fg);
}
.kv {
  display: grid;
  grid-template-columns: max-content minmax(0, 1fr);
  gap: 3px 12px;
  margin: 0;
}
.kv dt {
  color: var(--muted);
}
.kv dd {
  margin: 0;
  overflow-wrap: anywhere;
}
.warns {
  display: grid;
  gap: 6px;
  margin: 0;
  padding: 0 var(--gx) 10px;
  list-style: none;
}
@media (max-width: 640px) {
  .row-radio {
    grid-template-columns: minmax(0, 1fr);
  }
  .chk,
  .trust-warn {
    margin-left: 0;
  }
}
</style>
