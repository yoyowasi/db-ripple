# SP6 — 형식 변환(FOR JSON)

- **목적:** §6.1 값 표현 규칙이 오류 없이 값을 보존하는지, text/ntext/image를 AFTER 트리거에서 참조하면 언제 오류가 나는지 확인한다(A12, A19).
- **결과:** 미실행(2026-11-06 Docker 미설치)
- **증거 범위:** DB(SpikeLab_SP6, 끝에서 삭제).

## 방법

`spikes/sql/sp6_10_types.sql`(master에서 실행, 자체 생성·삭제). 경계값 1행 + 전부 NULL 1행.

- (A) 열마다 `FOR JSON PATH, INCLUDE_NULL_VALUES`를 변환 없이 실행해 성공/오류 번호를 남긴다.
- (B) §6.1 변환(xml→nvarchar, hierarchyid→ToString, geography/geometry→STAsText + `[c@srid]`, sql_variant→문자열 + `[c@type]`)으로 직렬화.
- (C) OPENJSON으로 되읽어 원값과 비교(`same` 1/0). decimal·bigint는 JSON 숫자 원문 문자열이 원값 문자열과 같은지 본다.
- (D) text 열 참조 트리거, `SELECT * FROM inserted` 트리거 생성 시도 → 생성되면 UPDATE로 실행 시점 확인.

## 예상과 판정

| 부분 | 예상 | 깨짐으로 보는 것 → 반영 |
|---|---|---|
| A | hierarchyid·geography·geometry 오류(CLR). 나머지 성공(binary류 base64, money·guid 문자열). xml·sql_variant·text류 결과는 기록만 | 예상 밖 오류 형식 → §6.1에 변환 규칙 추가 |
| B | 오류 없음, ISJSON = 1 | 오류 → 해당 형식 변환식 수정 |
| C | 모든 `same` = 1. 특히 decimal(38,10)·bigint 원문 일치, datetimeoffset 오프셋 유지, datetime2(7)·time(7) 7자리 유지, nvarchar(따옴표·역슬래시·서로게이트 쌍) 일치 | `same = 0` → 형식별 변환 규칙 수정(§13 '실패하면') |
| B_srid_attr | `cGeog@srid`가 숫자 4326, 키 이름 그대로 | 이름이 바뀌거나 오류 → backend-2와 속성 이름 재합의 |
| D | text 참조·`SELECT *`는 생성 시점 오류 311 예상 | 실행 시점에만 나면 생성기가 설치 직후 발동 검사를 해야 함. 어느 쪽이든 text류는 `unobservable`(§6.1 유지) |
