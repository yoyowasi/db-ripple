# 새 세션 시작 프롬프트 (orchestrate, 1주차)

저장소 폴더에서 새 Claude Code 세션을 열고 아래 블록을 그대로 붙여 넣는다. 2주차부터는 마지막 주의 보고와 `docs/architecture.md` §11.3을 보고 범위만 바꿔 쓴다.

```text
/orchestrate

졸업작품 "SQL 실행 전 변경 미리보기 도구"를 이 저장소의 docs/architecture.md(설계 v1.2)대로 구현하기 시작한다. 이번 세션의 범위는 1주차다. 문서로 확정하지 못한 전제를 스파이크로 실측해 결정 D1·D3·D4를 확정한다. 그다음 기준선 구축, 카탈로그, 감사 트리거 생성기 첫판, CascadeLab CL01~CL08(스키마와 손 정답)까지 만든다.

[먼저 읽을 것]
- docs/architecture.md: §0 요약, §5 데이터 흐름, §6 데이터 계약(감사 저장소 포함), §7 원인 귀속 규칙과 전제 H1~H5, §8 결정 D1~D9, §11.3 일정, §13 스파이크 SP0~SP10. §2 핵심 가정 표(A1~A24)는 필요한 행만 본다.
- 실측 결과가 설계와 다르면 코드보다 먼저 docs/architecture.md를 고친다. 바꾼 결정에는 날짜와 근거(스파이크 ID)를 적고 버전을 v1.3으로 올린다.

[조건]
- 1인·약 1달 일정의 1주차다. 2주차 이후 범위(분석기, 실행기, 프런트, 평가 하네스)는 만들지 않는다.
- 공개 샘플 DB만 쓴다: AdventureWorks(공식 .bak, MIT), pubs(내가 내려받는다, 저장소에 넣지 않는다), 직접 만드는 CascadeLab. 회사 업무 DB와 mssql·mssql-dotnet MCP 도구는 쓰지 않는다.
- SQL Server는 로컬 Docker 컨테이너 1개다.
  - 이미지는 mcr.microsoft.com/mssql/server 2025(CU 태그 고정), 설정은 MSSQL_PID=EnterpriseDeveloper, ACCEPT_EULA=Y, 메모리 4GB 이상, 포트는 localhost만.
  - SA 비밀번호 같은 비밀값은 커밋하지 않는 .env에 두고 출력하지 않는다.
  - SP0가 실패하면 2022-latest Developer로 바꾸고 D1을 고친다.
- 백엔드는 C#(.NET 10, ScriptDom 180.x, Microsoft.Data.SqlClient)이다. 이번 주에는 라이브러리·콘솔·테스트 프로젝트까지만 만든다.
- 문서화되지 않은 기능(%%physloc%%, fn_dblog 등)은 쓰지 않는다. 사용자 트리거의 본문과 순서는 바꾸지 않는다.
- Docker 이미지와 NuGet 패키지를 내려받고 컨테이너를 띄우는 것은 해도 된다. git commit·push와 GitHub 공개 작업은 내가 요청할 때만 한다.

[1주차 할 일] (docs/architecture.md §11.3, §13)
1. 1~2일: 스파이크
   - SP0(에디션, 파일별 스냅샷 생성)
   - SP4(ScriptDom 파서 클래스, GO 처리, 트리거 쓰기 대상 추출)
   - SP5(FILELISTONLY, 전체 텍스트 카탈로그, 백업·복원·스냅샷 시간, 스냅샷이 있을 때 RESTORE 거부)
   - SP6(FOR JSON 형식 변환)
   - SP10(트랜잭션 안 EXECUTE AS/REVERT, 소유권 체인으로 감사 쓰기와 NEXT VALUE FOR)
   - 손으로 쓴 감사 트리거 3~4개와 미니 스키마(CL06·CL07·CL09·CL13)로 SP1(H1~H5), SP2(a)(c)(d)(e), SP3
2. 스파이크마다 docs/spikes/SPn.md를 쓴다. 담을 내용은 목적, 방법(실행한 SQL·명령), 결과(확인한 사실과 추정을 나눠서), 증거 범위(DB 실측인지 정적인지), 설계 반영이다. 깨진 전제는 §7.1의 "깨지면" 규칙대로 문서에 반영한다.
3. 3~5일: 구현
   - docker compose
   - C7 카탈로그
   - C9 감사 트리거 생성기 첫판(형식 변환, 제어 테이블, 상한, 연산별 Last 지정, IDENTITY 금지, 마지막 문장 행 수 규칙)
   - C8 기준선 구축(스냅샷 삭제 → COPY_ONLY 백업 → RESTORE WITH MOVE → 설치 → 파일별 경로로 기준 스냅샷)
   - CascadeLab CL01~CL08 스키마와 손 정답(JSON: 변경 키 집합, 간선, 기대 오류)

[팀] 최종 구성은 orchestrate가 정한다. 제안은 다음과 같다.
- database-engineer: 스파이크 SQL, CascadeLab 스키마와 정답, 감사 트리거 생성 규칙
- backend-developer: .NET 솔루션 골격, C7·C8·C9
- devops-engineer: docker compose, 컨테이너 설정(.env 처리)
- qa-engineer: 스파이크 재현 스크립트, CascadeLab 정답과 실제 실행 결과 대조
- reviewer: 스파이크 결론이 증거와 맞는지, 설계 문서 갱신이 맞는지 독립 검토(작성자와 다른 에이전트)
같은 파일은 한 명만 고친다.

[완료 기준]
- SP0~SP6·SP10 결과가 docs/spikes/에 있고, D1·D3·D4와 H1~H5의 상태가 docs/architecture.md(v1.3)에 반영돼 있다.
- AdventureWorks에 감사 트리거가 설치되고 기준 스냅샷이 만들어졌다(실행 로그로 증거를 남긴다).
- CL01~CL08 스키마와 손 정답이 있고, 정답을 실제 실행 결과와 대조했다.
- dotnet build와 테스트가 통과한다.
- 보고에는 만든·고친 파일, 실행한 검증(명령, 결과, 증거 범위), 확인하지 못한 것, 내가 정해야 할 것을 적는다.
```
