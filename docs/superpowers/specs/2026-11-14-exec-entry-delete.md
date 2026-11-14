# EXEC 첫 독립 DELETE의 직접 귀속

사용자는 2026-11-14 로컬 DB 평가에서 발견한 CL22 귀속 불일치에 대해 분석 계획·수집 정보를 확장하여 해결하도록 승인했다.

## 목표와 접근

CL22의 `ChildA DELETE → Parent DELETE → ChildB FK DELETE`에서 실제 첫 ChildA 발동만 PROC_DIRECT/certain으로 입증한다. 원본·샌드박스 프로시저 정의나 실행 SQL을 계측용으로 바꾸지 않는다.

검토한 대안은 (1) 정적 WriteTargets 우선 판정, (2) 모듈 재작성과 런타임 marker, (3) 첫 독립 DELETE의 정적 조건과 실제 첫 감사 헤더를 결합하는 방법이다. (1)은 0행 DELETE 뒤의 실제 FK 삭제를 잘못 확정한다. (2)는 @@ERROR·중첩 호출·권한과 원복에 새로운 관측 부작용을 만든다. 이번 구현 선택은 필요한 범위가 작고 실행 동작을 보존하는 (3)이다.

## 내부 계약

- `ProcedurePlan`에 선택적인 첫 독립 DELETE 계획을 추가한다. 기본값은 없음이다.
- 전체 본문이 명시적으로 허용한 직선형 SQL뿐일 때만 계획을 만든다. 최소 지원은 SET NOCOUNT, 스키마가 명시된 단순 DELETE, 리터럴 RETURN이다. 분기·반복·GOTO·TRY/CATCH·트랜잭션 제어·중첩 호출·미지원 구문은 증거를 만들지 않는다.
- 첫 DELETE 대상은 기반 사용자 테이블이어야 하며 활성 사용자 DELETE 트리거, 자체 참조 또는 그 테이블 삭제가 유발하는 FK action이 없어야 한다. 감사 트리거 예외는 원시 카탈로그의 정확한 도구 트리거 이름과 AFTER 속성으로 확인한다. 이름 접두만으로 신뢰하지 않는다.
- manifest에 0행 직접 DELETE 헤더 보존 capability를 선택적으로 기록한다. 구형 manifest의 기본값은 false다.
- 수집기는 실제 raw 최초 헤더(0행 포함)를 문장 경계와 대조한 후 특정 FiringId에만 직접 귀속 근거를 전달한다. 실행 성공, 수집 완전성, 버전·대상 coverage, object/operation/depth와 `CaptureSeqRange[0]` 일치를 확인한다.
- 이후 같은 테이블의 발동에는 근거를 재사용하지 않는다. 첫 DELETE가 0행이면 그 뒤 발생한 FK 삭제는 기존 FK 판정을 유지한다.
- 계획·capability·수집 근거 중 하나라도 없거나 불일치하면 기존 판정으로 돌아간다. EXEC 적용 금지는 유지한다.

## 검증

1. CL22 실제 DB 평가에서 ChildA PROC_DIRECT/certain, ChildB FK_CASCADE 유지.
2. 첫 DELETE가 0행인 프로시저 뒤의 실제 cascade를 direct로 확정하지 않음.
3. 부분 직접 삭제와 뒤 cascade를 다른 발동으로 구분하며 근거 재사용 금지.
4. 조건문·중첩 호출·사용자 트리거·self FK·실패·누락 수집·구형 manifest는 보수적 fallback.
5. 전체 빌드·단위/DB 테스트, 기존 30사례 평가, EXEC 평가 및 API 거부 목록 회귀.

SQL Server는 유효한 DML에 영향 행이 없어도 DML 트리거가 발동한다고 명시한다. 실제 0행 경계도 DB 회귀로 검증한다. 근거: [Microsoft CREATE TRIGGER](https://learn.microsoft.com/en-us/sql/t-sql/statements/create-trigger-transact-sql?view=sql-server-ver17).
