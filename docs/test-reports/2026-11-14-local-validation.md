# 2026-11-14 로컬 검증 기록

대상은 `b1a6fab` 기반 `codex/validate-home-tests` 작업 트리다. 공개 AdventureWorks/CascadeLab과 새 로컬 Docker SQL Server만 사용했다. 원본 로그·TRX·가짜 HTTP fixture는 `logs/home-test/`에 로컬 보관하며 커밋하지 않는다. `.env`, 비밀번호, 백업과 미리보기 행 덤프도 PR에서 제외한다.

## 환경과 실행

- .NET SDK 10.0.402, Node 22.15.0/npm 11.2.0, PowerShell 5.1 및 7.6.9.
- Docker 28.4.0, SQL Server 2025 CU10 `17.0.5035.5`, Enterprise Developer, 루프백 포트 14333.
- 테스트용 비밀번호를 무작위 생성하여 로컬 `.env`에만 저장했다. AdventureWorks 공식 백업 50,229,248 bytes를 복원했다(SHA256 `FA6A2A5D431AD88123F89B36B1F2C7E42CA4BDF6B293269A44DF80F6DE3738A5`).
- 초기 샌드박스의 VSTest/testhost 연결 및 npm 네트워크 실패는 실행 권한을 허용한 동일 명령으로 재실행해 해결했다. 제품 실패와 구분해 로그를 보존했다.

## 기존 코드에서 재현한 문제와 조치

1. **테스트 스크립트:** 점유된 N1 포트의 다른 Host를 자신의 Host로 오인하고 refresh를 전송했다. Env 생략·실패 뒤 상속된 연결값을 신뢰했고, DB/API를 건너뛰어도 실측으로 표시했다. 포트 선검사, 이번 Env의 검증된 루프백 연결만 전달, 실제 수행 범위 요약으로 수정했다.
2. **실제 HTTP/SQL 실행:** PowerShell의 배열 열거로 요청 바이트가 `Object[]`가 되어 분석 JSON이 잘못 전송됐다. `[byte[]]`로 타입을 명시했다. 비 Unicode SQL 문자열 때문에 존재하는 AW가 `AW_Origin??`로 반환되던 문제도 N 문자열로 수정했다.
3. **과거 테스트의 범위:** CL09~20용 테스트가 신규 CL21~25까지 선택해 거부 사례를 실제 실행했다. CL25b의 COMMIT이 공유 fixture를 오염시켜 후속 테스트가 연쇄 실패했다. legacy 범위를 명시하고, 신규 EXEC 수락·정확한 거부 규칙과 private Reach DB 검증을 별도로 추가했다. 오라클은 생산 평가와 동일하게 거부 사례를 실행하지 않는다.
4. **손 정답:** 실제 카탈로그 분석의 CL23/24 경고와 CL25c의 두 거부 진단을 기록했다. 별도 임시 DB에서 CL24의 오류 547·호출 오류 266 및 `ERROR_PROCEDURE() = cl24.usp_Withdraw`를 확인했다. 정정 근거·날짜는 각 사례의 `expected.corrections`에 남겼다.
5. **간접 COMMIT 테스트:** 도달 폐포가 이미 간접 트리거도 사전 거부하도록 변경되어 있었지만 테스트는 실행·오염을 기대했다. 정확한 사전 거부와 양 DB 불변, 다음 정상 미리보기를 검증하도록 갱신했다.
6. **CL22 귀속:** 초기 실제 평가에서 직접 ChildA 삭제를 FK inferred로 판정했다. 분석 계획의 쓰기 집합만으로는 0행 직접 DELETE와 뒤 FK 삭제를 구분할 수 없다. 사용자 승인으로 [첫 독립 DELETE의 실행 증거](../superpowers/specs/2026-11-14-exec-entry-delete.md)를 확장했다. 새 실제 DB 회귀에서 변경 전 전량·부분 삭제 2건 실패와 0행·조건부 fallback 2건 통과를 먼저 확인했다.

## 현재 확보한 검증

| 검증 | 결과·증거 범위 | 로컬 근거 |
|---|---|---|
| 변경 전 .NET 빌드·DB 없는 테스트 | 경고/오류 0, 1029 통과, 59 DB 건너뜀 | `2026-11-14/qa/` |
| 스크립트 회귀 | PS5.1·7 각각 23/23. 실제 localhost HTTP JSON·한국어·개행 포함 | `2026-11-14/setup/scripts-ps51.log`, `scripts-ps7.log` |
| 기본 평가 | 30사례 × 3회, 제품+B0/B1/B2. 이전과 공통인 103지표 동일, B1 신규 33지표 | `2026-11-14/baseline-eval/` |
| 기본 제품 핵심 지표 | M1 정밀도·재현율 1.0000, M2 ALL 재현율 1.0000, M3 certain 과신 0, M4 안정성 1.0000 | 같은 실행 `metrics.csv`, `vs-baseline.csv` |
| EXEC 포함 분석 API | AW 4/4, CascadeLab 36/36, RT 1/1, Reach 1/1(두 문장) | `2026-11-14-n1-final/summary.md`, `n1/*/verdict.txt` |
| 웹 설치·타입 검사·빌드 | `npm ci`, `npm run build` 종료 0 | `2026-11-14/web/` |

새 CL22 구현 이후 전체 테스트와 재평가 결과는 최종 검증에서 추가한다. 위 기본 평가·N1은 CL22 확장 전 확보한 결과이므로 최종 변경 전체의 통과 근거로 대신하지 않는다. **2026-11-15 클라우드 인계:** 신규 확장은 빌드 경고/오류 0이지만 직렬화 호환성 테스트 11건이 남은 WIP다. 정확한 상태·명령·이어 할 일은 [클라우드 인계 문서](2026-11-15-cloud-handoff.md)에 기록했다.

## 남은 범위와 알려진 경고

- 웹 빌드는 AG Grid chunk 1,178.19 kB(gzip 334.89 kB) 경고를 낸다.
- npm audit moderate 3항목은 `vue-tsc → @vue/language-core → minimatch` 개발 의존성 경로의 한 DoS advisory 영향이다. `npm audit --omit=dev`는 0건이다. 의존성을 자동 수정하지 않았다.
- Split(2022 원본 컨테이너), SP11~21의 수동 실험, 게시본/브라우저 UX, 대량 성능·M5는 이번 자동 검증의 범위 밖이다. SP17 변경은 개발 Host 명령 수정이며 Split 성공을 뜻하지 않는다.
- 원본 공유 DB는 공개 샘플 `AW_Origin`, `CascadeLab`, `CascadeLab_RT`, `CascadeLab_Reach`다. 통합 테스트·평가의 임시 DB는 private `dbrt_` 접두로 만들고 각 실행이 정리한다. 컨테이너와 공개 샘플은 후속 확인을 위해 유지한다.

## 클라우드 후속 기록

위 로컬 수치를 덮어쓰지 않는다. 2026-11-15 클라우드에서 새로 실행한 수정·검증과 실제DB/SP7/평가/N1 결과는 [코드 검증](2026-11-15-cloud-validation.md) 및 [DB 검증](2026-11-15-cloud-db-validation.md)에 별도로 기록한다.
