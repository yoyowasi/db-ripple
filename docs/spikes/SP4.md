# SP4 ScriptDom 스파이크 결과

## 목적
설계 §13 SP4: ScriptDom 180.140.0에서 쓸 파서 클래스, GO·주석·문자열 처리, 오류 위치, 문장 위치, 트리거 본문 쓰기 대상 추출을 DB 없이 확인한다.

## 방법
- 프로젝트: `spikes/dotnet/Sp4.ScriptDom.Tests` (xUnit, net10.0, `Microsoft.SqlServer.TransactSql.ScriptDom` 180.140.0)
- 실행: `dotnet test spikes/dotnet/Sp4.ScriptDom.Tests` (28개 통과)
- 테스트 클래스: `ParserClassTests`(항목 1), `BatchAndPositionTests`(항목 2~4), `TriggerAnalyzerTests` + `TriggerAnalyzer.cs`(항목 5), `SqlLintTests`(항목 6)
- 증거 범위: 정적(라이브러리 실행). DB 미사용.

## 결과 (확인한 사실)
1. 파서 클래스: TSql80/90/100/110/120/130/140/150/160/**170/180**Parser, TSqlFabricDWParser, 기반 TSqlParser가 있다. 최고는 **TSql180Parser**. `SqlVersion`에 Sql170, Sql180이 있다.
   - 160 vs 170/180 차이: `CREATE JSON INDEX`, `CREATE VECTOR INDEX`, `CREATE EXTERNAL MODEL`, `AI_GENERATE_EMBEDDINGS(... USE MODEL ...)`는 160에서 오류, 170·180에서 통과.
   - 차이 없음(160·180 모두 통과): JSON 컬럼 타입, IS DISTINCT FROM, MERGE, UPDATE ... OUTPUT, CREATE TRIGGER.
2. GO 처리 (`TSqlScript.Batches`, 160 기준; 170도 같은 결과):
   - 블록 주석·줄 주석·문자열 리터럴 안의 GO는 구분자로 보지 않는다(정상).
   - 소문자 `go`, 들여쓰기·뒤 공백, 뒤에 줄 주석·블록 주석이 붙은 GO는 구분자로 인식한다.
   - 마지막 GO 없음: 마지막 배치가 유지된다.
   - **`GO 2`는 파싱 오류**(46010, 2:4). 횟수는 지원하지 않는다.
   - **줄 중간의 `SELECT 1 GO`는 구분자가 아니다.** GO가 열 별칭으로 파싱돼 한 배치 2문장이 된다(오류 없음).
3. 오류 위치: `ParseError.Number`(예: 46010), `Line`, `Column`, `Offset`, `Message`. 두 번째 배치의 오류도 **스크립트 전체 기준 줄·열·오프셋**이다(예: 4:1, offset 21). 오류가 있어도 Batches는 나뉘어 반환된다. `Message`는 이 PC 로케일(한국어)로 나온다.
4. 문장 위치: `StartLine`·`StartColumn`(1부터)·`StartOffset`·`FragmentLength`로 원문을 정확히 잘라낼 수 있다(CRLF 포함). 끝 줄·열은 `ScriptTokenStream[LastTokenIndex]`의 Line·Column(+토큰 길이-1)로 구한다. 두 번째 배치 문장의 줄도 전체 기준이다. 문장 끝에 `;`가 있으면 span에 포함된다.
5. 트리거 쓰기 대상 추출(`TriggerAnalyzer`, 손으로 쓴 AW 유사 표본 3종 + 동적/외부 표본):
   - INSERT/UPDATE/DELETE/MERGE 대상을 스키마 포함 이름으로 추출. `UPDATE h ... FROM Sales.SalesOrderHeader h JOIN inserted i`는 `Sales.SalesOrderHeader`로 풀림.
   - 제외: inserted/deleted, CTE 이름(`WITH c ... DELETE FROM c`), 테이블 변수(`@tv`), `#tmp`.
   - 플래그: InsteadOf, Rollback, Raiserror, DynamicSql(`EXEC(@sql)`, `sp_executesql`, `EXEC @proc`), ProcedureCalls(쓰기 집합 미상), External(4부분 이름, OPENQUERY, OPENROWSET, `xp_` 접두사, msdb 참조).
   - iduSalesOrderDetail 유사: {Person.Person, Production.TransactionHistory, Sales.SalesOrderHeader}, ROLLBACK, 호출 dbo.uspLogError. uSalesOrderHeader 유사: {SalesOrderHeader, SalesPerson, SalesTerritory}. dEmployee 유사: INSTEAD OF, RAISERROR, ROLLBACK, 쓰기 대상 없음.
   - 동적 SQL 문자열 안의 쓰기는 정적으로 보이지 않는다(테스트로 확인). DynamicSql 플래그로만 알린다.
6. 정적 문법 점검(`SqlLintTests`): `spikes/sql/**/*.sql`과 `cascadelab/**/*.sql`을 TSql170Parser(대상 2025, 호환성 수준 170)로 파싱한다. 최종 결과는 오케스트레이터 통합 점검(아래 "통합 점검")을 본다. `cases/*.json` 안의 SQL 문장은 점검 대상이 아니다.

## 추정 (테스트로 직접 확인하지 않음)
- 문자열·주석 안 GO 무시는 토크나이저 수준 동작이라 큰 스크립트에서도 같다고 본다.
- 170/180 간 차이는 확인하지 않았다(JSON/VECTOR 구문은 170·180 둘 다 통과).

## 설계 반영 제안
- **D1 파서**: `TSql180Parser`를 기본으로 하고, 허용 목록이 DML 세 종류라 차이 영향은 작다. 문서 근거가 약한 SQL Server 2022(160) 대상이면 160도 가능하나, 160은 신규 구문(JSON/VECTOR INDEX 등)을 거부하므로 "모르는 구문은 거부" 원칙과 맞는다. 대상 서버 버전에 맞춰 선택(결정 필요).
- **D5 GO 분리**: ScriptDom Batches를 쓴다. 단 (a) `GO n`은 오류이므로 사전에 거부하거나 별도 전처리, (b) 줄 중간 GO는 구분자가 아니라 별칭으로 파싱되므로 "GO는 줄 단독" 규칙을 문서화한다. sqlcmd 대체 규칙은 필요 없다. 오류 줄·열은 전체 기준이라 그대로 화면에 쓸 수 있다.
- **C7 쓰기 대상 규칙**: `TriggerAnalyzer`의 visitor를 이식한다. 별칭은 FROM의 NamedTableReference.Alias로 풀고, inserted/deleted·CTE·`@`·`#`는 제외한다. DynamicSql·ProcedureCalls·External·Rollback 플래그가 하나라도 있으면 쓰기 집합을 "미상/부분"으로 표시한다.
- 제품 코드에서는 파싱 오류 시 닫힘(거부)로 처리한다(본 테스트의 Analyze도 예외).

## 미확인 항목
- 실제 AW 트리거 10개 추출(DB 복원 뒤).
- 4부분 이름·msdb 판정에서 변수·뷰·동의어 해석(정적으로 불가).
- 스키마 생략 이름(`dbo` 기본 스키마) 정규화는 미구현. 현재는 입력 그대로 낸다.
- `EXEC dbo.uspX`의 실제 쓰기 집합(프로시저 본문 분석 필요).
- 문법 점검은 구문만 본다. 객체 존재, 권한, 실행 결과는 DB에서 실행해야 확인된다.
