# SP6 — 형식 변환(FOR JSON)

- **목적:** §6.1 값 표현 규칙이 오류 없이 값을 보존하는지, text/ntext/image를 AFTER 트리거에서 참조하면 언제 오류가 나는지 확인한다(A12, A19).
- **결과:** **깨짐(부분)** — 2026-11-10 실행. 오류는 geography/geometry 무변환(13604)뿐이고 §6.1 변환에는 오류가 없다. 그러나 float·real 값, 공간 형식의 Z/M, sql_variant의 형식은 보존되지 않는다. text류는 트리거 생성 시점에 311이 난다. 생성기 모양 트리거는 설치·발동·기록이 된다. 단 앞부분이 같은 열(`[a]`와 `[a.b]`)이 함께 있거나 `@srid`와 충돌하는 열 이름이 있으면 생성 시점에 13601로 실패한다. `.`가 든 열만 있으면 실패하지 않고 중첩 객체(`{"a":{"b":20}}`)가 되어 키 대응이 깨진다(2026-11-10 독립 검토로 문구 정정).
- **증거 범위:** DB 실측(컨테이너 dbripple-sql, SQL Server 2025 RTM-CU10 17.0.5035.5, 2026-11-10). SpikeLab_SP6는 끝에서 삭제했다. 제품 생성기 코드는 실행하지 않고, 같은 모양을 손으로 옮겨 실측했다.

## 방법

1. `spikes/sql/sp6_10_types.sql`(master에서 실행, 자체 생성·삭제). 경계값 1행 + 전부 NULL 1행.
   - (A) 열마다 `FOR JSON PATH, INCLUDE_NULL_VALUES`를 변환 없이 실행해 성공/오류 번호를 남긴다.
   - (B) §6.1 변환(xml→nvarchar, hierarchyid→ToString, geography/geometry→STAsText + `[c@srid]`, sql_variant→문자열 + `[c@type]`)으로 직렬화한다.
   - (C) OPENJSON으로 되읽어 원값과 비교한다(`same` 1/0). decimal·bigint는 JSON 숫자 원문 문자열이 원값 문자열과 같은지 본다.
   - (D) text 열 참조 트리거, `SELECT * FROM inserted` 트리거 생성 시도 → 생성되면 UPDATE로 실행 시점 확인.
2. `spikes/sql/sp6_20_generator_shape.sql`(2026-11-10 추가): `-Part 1` → `common_audit_store.sql`(`-Database SpikeLab_SP6`) → `-Part 2`.
   - (E) C9 `AuditTriggerGenerator.TriggerSql`(U)과 `AuditValueExpressions.SelectItems`의 모양을 그대로 옮긴 `[dbo].[dbr_audit_Types_U]`(Last)를 만든다. `UPDATE dbo.Types SET cBit = cBit`(3행)로 발동한 뒤 INSERTED 이미지를 OPENJSON으로 되읽어 원값과 비교한다. 행 3에는 공간 Z/M·17자리 좌표, float 0.1+0.2, 최솟값 경계, 제어 문자(NCHAR(0)·탭·줄바꿈)를 넣었다. 생성기와 다른 점은 tableObjectId 리터럴 대신 `OBJECT_ID`를 쓴 것 하나다(값은 같다).
   - (F1) float·real의 FOR JSON 원문 자릿수와 되읽기를 스타일 3(17자리)과 대조한다.
   - (F2) sql_variant 기저 형식 18개를 변환 없이 FOR JSON으로 직렬화한다.
   - (F3) 시스템 CLR 형식에 `CONVERT(nvarchar(max))`를 적용한다(사용자 CLR UDT 경로의 대리 확인).
   - (F4) 열 이름 `[a.b]`, `[a]`+`[a.b]`, geography `g` + 실제 열 `[g@srid]`, 따옴표·역슬래시 이름을 시험한다.
3. 실행(PowerShell, 저장소 루트). `-ContinueOnError`는 쓰지 않았다. 의도한 오류는 모두 스크립트 안 TRY/CATCH로 받았다. 최종 로그 4개는 모두 첫 줄이 `exit=0`이다.
   ```
   .\scripts\run-sql.ps1 spikes\sql\sp6_10_types.sql -Log spikes\logs\sp6-10-types.log
   .\scripts\run-sql.ps1 spikes\sql\sp6_20_generator_shape.sql -Part 1 -Log spikes\logs\sp6-20-part1.log
   .\scripts\run-sql.ps1 spikes\sql\common_audit_store.sql -Database SpikeLab_SP6 -Log spikes\logs\sp6-20-common-audit-store.log
   .\scripts\run-sql.ps1 spikes\sql\sp6_20_generator_shape.sql -Part 2 -Database SpikeLab_SP6 -Log spikes\logs\sp6-20-part2.log
   ```

**스크립트 수정과 재실행(2026-11-10)**

- 1차 실행: sqlcmd 기본값이 `QUOTED_IDENTIFIER OFF`라서 (C)의 xml `.value()`(base64 복원)가 1934로 실패했다(`sp6-10-types-run1-qi-off.log:36`). → 첫 배치에 `SET QUOTED_IDENTIFIER ON`을 넣었다. 제품 연결(SqlClient)의 기본값과 같다.
- 2차 실행: (C)가 float를 `float`로 바로 읽다가 `Msg 8115 ... Arithmetic overflow error converting expression to data type float.`로 SELECT 전체가 실패했다. 그래서 다른 형식의 비교가 나오지 않았다(`sp6-10-types-run2-float-overflow.log:86`). 이 오류 자체가 측정 결과(float 값 손실)다. → float만 원문(nvarchar)으로 읽고 `TRY_CONVERT`로 비교하도록 분리했다. 판정 기준(`same = 1`)은 바꾸지 않았다.
- (B)의 JSON이 sqlcmd 표시폭(256자)에서 잘려서, 행·키별 출력(`B_converted_kv`)을 추가했다. (C)에는 geometry 비교와 float·real 원문 대조(`C_float_text`)를 추가했다. 한글 열 이름 리터럴은 `N''`로 바꿨다(로그의 `??` 방지).
- QUOTED_IDENTIFIER ON 재실행: 오케스트레이터가 `run-sql.ps1`에 `-I`를 넣은 뒤(09:07:58), 두 스크립트를 09:21에 다시 실행했다. 결과는 이전과 같다. 실험 트리거는 `uses_quoted_identifier = 1`로 저장됐다(`sp6-20-part2.log:9` `E_module_qi dbr_audit_Types_U 1`). 이 재실행 전에도 두 스크립트는 첫 배치에서 `SET QUOTED_IDENTIFIER ON`을 했다. 아래 인용은 재실행 로그 기준이다.

## 예상과 판정

| 부분 | 예상 | 깨짐으로 보는 것 → 반영 |
|---|---|---|
| A | hierarchyid·geography·geometry 오류(CLR). 나머지 성공(binary류 base64, money·guid 문자열). xml·sql_variant·text류 결과는 기록만 | 예상 밖 오류 형식 → §6.1에 변환 규칙 추가 |
| B | 오류 없음, ISJSON = 1 | 오류 → 해당 형식 변환식 수정 |
| C | 모든 `same` = 1. 특히 decimal(38,10)·bigint 원문 일치, datetimeoffset 오프셋 유지, datetime2(7)·time(7) 7자리 유지, nvarchar(따옴표·역슬래시·서로게이트 쌍) 일치 | `same = 0` → 형식별 변환 규칙 수정(§13 '실패하면') |
| B_srid_attr | `cGeog@srid`가 숫자 4326, 키 이름 그대로 | 이름이 바뀌거나 오류 → backend-2와 속성 이름 재합의 |
| D | text 참조·`SELECT *`는 생성 시점 오류 311 예상 | 실행 시점에만 나면 생성기가 설치 직후 발동 검사를 해야 함. 어느 쪽이든 text류는 `unobservable`(§6.1 유지) |

## 결과

### 판정

| 부분 | 판정 | 근거(로그 줄) |
|---|---|---|
| A | **성립**(예상 밖 오류 없음). 다만 예상과 다른 성공·형식이 있다: hierarchyid는 오류 없이 문자열이 되고, money는 JSON 숫자로 나온다 | `sp6-10-types.log:12` `A_no_conversion cGeog geography 0 13604 FOR JSON cannot serialize CLR objects. ...`(geometry는 13번째 줄, 같은 오류), `:11` `A_no_conversion cHier hierarchyid 1 NULL [{"Id":1,"cHier":"\/1\/2\/"},...]`, `:14` `A_no_conversion cMoney money 1 NULL [{"Id":1,"cMoney":922337203685477.5807},...]` |
| B | **성립** | `sp6-10-types.log:35` `B_converted_json 985 1` |
| C | **깨짐**: float만 `same = 0`이다. 나머지 18개 형식은 두 행 모두 `same = 1` | `:94` `C_roundtrip 1 float 0`, `:128` `C_float_text 1 1.797693134862316e+308 1.7976931348623157e+308 NULL ...` |
| B_srid_attr | **성립**: `cGeog@srid`는 숫자 4326이고 키 이름도 그대로다 | `:132` `B_srid_attr cGeog@srid 4326 2` |
| D | **성립**: text 열 참조와 `SELECT * FROM inserted` 모두 **생성 시점**에 311이 난다. 트리거가 만들어지지 않아 실행 시점 행은 없다 | `:137`·`:138` `... 0 311 Cannot use text, ntext, or image columns in the 'inserted' and 'deleted' tables.` |
| E(추가) 생성기 모양 | **깨짐(부분)**: 생성·발동·헤더·이미지는 정상이다. float(행 1·3)과 공간 Z/M(행 3)은 보존되지 않는다 | `sp6-20-part2.log:14` `E_fire NULL NULL 1`, `:17` `E_header 1 [dbo].[Types] U 3 3 1 2 FULL`, `:21` `E_image_count INSERTED 3 3 3`, `:57`·`:103` `E_roundtrip … float 0`, `:104`~`:107` `geography STEquals 1`·`이진 동일 0`(geometry도 같음) |

§13 SP6 통과 기준("오류가 없고 값이 보존된다") → **깨짐**이다. §13 '실패하면'(형식별 변환 규칙 수정)을 적용한다(아래 제안 참고).

### 확인한 사실

1. **FOR JSON은 float를 유효 숫자 16자리로, real을 8자리로 낸다.** 17자리(스타일 3)가 필요한 값은 바뀐다.
   - float 최댓값은 `1.797693134862316e+308`로 나온다. 되읽으면 범위를 넘는다(`TRY_CONVERT`는 NULL, 직접 읽으면 8115).
   - F1 float 표본 10개 중 5개가 불일치했다: `sp6-20-part2.log:130` `F1_float 0.1+0.2 3.0000000000000004e-001 3.000000000000000e-001 0 1`, `:132` max, `:134` min normal, `:136` `123456789.123456789`. 스타일 3으로 되읽으면 10개 모두 일치한다.
   - real 표본 6개 중 1개가 불일치했다: `:147` `F1_real 1000+2^-14 1.0000000610351562e+003 1.0000001e+003 0 1`. 스타일 3(float 경유)으로는 6개 모두 일치한다.
2. **geography/geometry의 `STAsText()`는 Z·M 값을 버린다.** 좌표는 17자리까지 나온다(`:27` `POINT (126.97801234567891 37.566512345678909)`). 그래서 행 3은 `STEquals`가 1이지만 이진 비교는 0이다(`:104`~`:107`). `ToString()`(= `CONVERT(nvarchar(max), 열)`)은 Z·M을 포함하고(`:177` `... 10.5 2.25`), `STGeomFromText(text, srid)`로 되읽으면 이진까지 같다(`:126`·`:127` `E_spatial_alt … 1 1`).
3. **sql_variant를 변환 없이 FOR JSON으로 내면 기저 형식 18개 모두 오류 없이 기저 값만 나온다.** 형식 정보는 남지 않는다(`:156` `F2_variant 7 datetime 1 NULL {"v":"2026-11-06T11:20:00.123"}`, `sp6-10-types.log:27` decimal → `1.25`). 기저 형식이 float면 1과 같이 16자리로 나온다(`sp6-20-part2.log:153`). 원 형식을 알면 값은 복원된다(`:115` `sql_variant(원 형식을 알 때) 1`).
4. **money·smallmoney는 JSON 숫자**(소수 4자리)로 나온다. A19와 §6.1의 "money는 문자열"과 다르다(`sp6-10-types.log:14`, `sp6-20-part2.log:31` `cMoney -922337203685477.5808 2`, type 2 = 숫자). 원문은 money 값과 같다(`money 원문` 세 행 모두 1).
5. **hierarchyid·xml은 변환하지 않아도 문자열이 된다.** 값은 `ToString()`·`CONVERT(nvarchar(max))` 결과와 같다(`sp6-10-types.log:10`·`:11`). CLR 오류(13604)는 geography·geometry에서만 난다.
6. decimal(38,10)·bigint의 JSON 숫자 원문은 양 끝 경계를 포함해 원값 문자열과 같다(`decimal(38,10) 원문`·`bigint 원문` 모두 1).
7. datetime2·datetimeoffset·time의 값은 보존된다. 그러나 **끝자리가 0인 소수 초는 생략된다**(`sp6-20-part2.log:37` `cDt2 0001-01-01T00:00:00`). datetimeoffset의 오프셋(+09:00, −14:00)은 유지된다.
8. nvarchar(따옴표·역슬래시·서로게이트 쌍·NCHAR(0)·탭·줄바꿈)와 빈 varbinary(0x → `""`)는 왕복해도 같다(`:111` `E_roundtrip 3 nvarchar 1`).
9. **열 이름:** FOR JSON PATH는 `.`를 경로로 해석해 `[a.b]`를 `{"a":{"b":20}}`로 만든다(`:182`). `[a]`와 `[a.b]`가 함께 있거나, geography `g` 옆에 실제 열 `[g@srid]`가 있으면 13601이 난다(`:183`·`:184`). 같은 모양의 트리거는 **생성 시점**에 13601로 만들어지지 않는다(`:186`). 따옴표·역슬래시는 키에서 이스케이프된다(`:185`).
10. BEGIN TRAN 안에서 생성기 모양 트리거가 발동하면 헤더는 `tranCount = 2`, `nestLevel = 1`이다(`:17`).
11. text/ntext/image는 트리거 밖의 FOR JSON에서는 문자열·base64로 나온다(`sp6-10-types.log:30`). 트리거 안에서 참조하면 생성 시점에 311이 난다(D).
12. QUOTED_IDENTIFIER ON에서 다시 실행해도 xml·hierarchyid·공간 형식을 포함한 모든 결과가 같았다(재실행 로그 기준).

### 추정

- real의 8자리 출력은 일부 값만 바꾼다. 1,000~1,024처럼 10진 8자리 간격이 real 간격보다 큰 구간에서 반례가 생긴다(계산 추정, 실측은 표본 6개).
- 사용자 CLR UDT의 `CONVERT(nvarchar(max), 열)`은 시스템 CLR 형식에서 되는 것으로 보아 될 가능성이 높다. 다만 값 보존 여부는 UDT의 ToString 구현에 달려 있다(실측 아님).

### 설계 반영 제안 (오케스트레이터가 architecture.md에 반영)

1. §6.1 표
   - float는 `CONVERT(varchar(30), 열, 3)`(문자열, 17자리), real은 `CONVERT(varchar(30), CONVERT(float, 열), 3)`로 바꾼다. FOR JSON 기본 숫자는 값을 잃는다.
   - money·smallmoney의 "FOR JSON에서 이미 문자열"을 "JSON 숫자(소수 4자리), 숫자 원문 규칙 대상"으로 고친다. uniqueidentifier가 문자열이라는 기술은 맞다.
   - geography·geometry는 `STAsText()` 대신 `ToString()`(Z/M 포함) + `[열@srid]`로 바꾼다.
   - sql_variant 행을 추가한다: 기저 값 + `[열@type]`(`SQL_VARIANT_PROPERTY(열, 'BaseType')`). 기저 형식이 float/real이면 위 규칙을 따른다.
   - datetime 계열: FOR JSON은 끝자리 0 소수를 생략하므로 표시 자릿수는 `sqlType`의 scale로 맞춘다.
   - text/ntext/image: "참조하면(SELECT * 포함) CREATE TRIGGER 시점에 311"을 근거로 확정한다.
   - 열 이름 규칙을 추가한다: `.`가 든 열이나 생성 접미사(`@srid`, `@type`)와 겹치는 열이 있는 테이블은 생성기가 미리 걸러 낸다. 제외 사유(예: `JSON_KEY_CONFLICT`)로 남기거나 키를 바꾼다. 충돌 열을 그대로 두면 설치가 13601로 실패하고, `.` 열만 있으면 설치는 되지만 이미지 키가 틀어진다.
2. A19를 정정한다: CLR 오류는 geography·geometry뿐이다(hierarchyid는 문자열). money는 숫자다. float/real에는 자릿수 제한이 있다.
3. §6.2 `tranCount`: 샌드박스 실행(BEGIN TRAN 안)에서는 2가 정상값이다(SP10에서도 같음).
4. C9 변경 요청은 2026-11-10 backend-3에게 보냈다(위 1의 float/real, 공간, sql_variant, 열 이름). backend-3는 같은 날 반영을 보고했다. 내용은 float·real 스타일 3, 공간 `ToString()` + `@srid`, sql_variant `@type`(기저 float/real은 스타일 3), `.` 이름(`JSON_KEY_DOT`)·생성 속성 충돌(`JSON_KEY_CONFLICT`) 열 제외다. 근거는 backend-3의 통합 테스트 `tests/DbRipple.Core.Tests/Capture/AuditInstallIntegrationTests.cs`이고, 이 스파이크에서 다시 실측하지는 않았다.

### 미확인

- 사용자 CLR UDT: 서버 수준 설정(`clr enabled`, `clr strict security`) 변경과 어셈블리 등록이 필요하다. 이번 승인 범위(실험 DB만 변경) 밖이라 측정하지 않았다. 생성기의 `TODO(SP6)`·`unverified` 표시는 유지한다.
- 곡선형(CIRCULARSTRING 등)·FULLGLOBE·빈 공간 값, float 전체 범위, real의 9자리 보장은 표본 밖이다.
- KEYS 모드 이미지는 SP6 범위 밖이라 보지 않았다.
