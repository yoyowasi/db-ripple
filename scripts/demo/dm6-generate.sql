/* DM6 — CascadeLab Big 데이터 준비(설계 §10.7 DM6, M5와 같은 cl20 틀). 데모 전용, 로컬 CascadeLab 원본에서만 실행한다.
   PART 1: Parent 5만·Child 10만을 만든다. 그 뒤 Host(원본 CascadeLab)에서 POST /api/sandbox/refresh로 기준선을 다시 만든다.
           데모 문장 DELETE FROM cl20.Parent WHERE id <= 50000; → Parent 5만 + Child 10만(FK_CASCADE) + ChildLog 10만(TRIGGER) = 행 이미지 25만.
           기본 상한 10만을 넘으므로 KEYS 모드·정확한 건수·status truncated·[적용] 차단을 보인다(D8).
   PART 2: 시드 크기(@parents = 3)로 되돌린다. 그 뒤 다시 refresh한다.
   실행: .\scripts\run-sql.ps1 scripts\demo\dm6-generate.sql -Database CascadeLab -Part 1 -Log logs\week3\dm6-generate.log
         .\scripts\run-sql.ps1 scripts\demo\dm6-generate.sql -Database CascadeLab -Part 2 -Log logs\week3\dm6-reset.log
   cl20.usp_Generate는 cascadelab/sql/cl20.sql에 있다(멱등 재생성). */
SET NOCOUNT ON;
IF DB_NAME() <> N'CascadeLab'
    THROW 50000, N'dm6-generate.sql runs only on the local CascadeLab origin.', 1;
EXEC cl20.usp_Generate @parents = 50000;
GO
-- PART 2 시드 크기로 되돌리기
SET NOCOUNT ON;
IF DB_NAME() <> N'CascadeLab'
    THROW 50000, N'dm6-generate.sql runs only on the local CascadeLab origin.', 1;
EXEC cl20.usp_Generate @parents = 3;
GO
