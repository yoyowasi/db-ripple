# EXEC Entry DELETE Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** CL22 직접 자식 삭제를 실제 첫 감사 발동에 근거하여 올바르게 귀속한다.

**Architecture:** 선택적 분석 계획과 0행 수집 capability를 결합한다. 수집기가 검증한 첫 발동의 근거만 C13에 전달하고, 모든 미지원·누락 경로는 기존 판정을 유지한다.

**Tech Stack:** .NET 10, ScriptDom, SQL Server 2025, xUnit.

**Spec:** ../specs/2026-11-14-exec-entry-delete.md

## Global Constraints

- 원본 프로시저·실행 SQL 변경 없음. EXEC Apply 금지 유지.
- 0행 헤더 보존, 특정 첫 FiringId만 증거 사용, 구형 데이터 기본 fallback.
- 분석기 거부 규칙 및 기존 FK 검증 완화 금지.
- 테스트 DB는 로컬 Docker와 private dbrt_ 접두만 사용. 비밀값 기록 금지.

## Review Focus

- 0행 직접 DELETE 뒤의 실제 FK 삭제를 직접 원인으로 오인하지 않음.
- 부분 직접 삭제 뒤 같은 테이블의 두 번째 발동에 근거를 재사용하지 않음.
- 사용자 트리거·self FK·분기·호출·트랜잭션 제어는 계획을 만들지 않음.
- 누락 헤더·구형 manifest·실행 실패에는 새 certainty를 부여하지 않음.
- 수집 정보 확장이 기존 JSON/fixture 소비자를 깨뜨리지 않음.

### Task 1: 분석·수집·귀속의 선택적 증거 연결

**Owner:** attribution_1; Contracts/Analysis/Core Capture manifest/Execution collector/Attribution 및 해당 단위 테스트의 필요한 파일만.

- [x] 실패하는 직접/0행/부분/fallback 회귀 작성 및 실패 확인.
- [x] 선택 계획·manifest capability·수집 증거를 연결하고 first-only 판정 구현.
- [x] 관련 Analysis/Execution/Attribution 및 계약 직렬화 테스트 실행.
- [x] 변경 파일과 검증 로그를 root에 전달. 독립 리뷰 후 지적 수정.

### Task 2: 실제 DB 회귀와 최종 PR 검증

**Owner:** root; 새 Integration 회귀 파일, 설계·검증 보고서, Git 브랜치/PR.

- [x] 실제 CL22·0행·부분 직접 삭제·조건문 fallback의 pipeline 검증 작성.
- [x] 전체 빌드 및 DB 테스트(SP7 포함), 기본/EXEC 평가, N1 API 회귀 실행.
- [x] 독립 리뷰와 검증 결과를 반영하고 allowlist 파일만 커밋·푸시.
- [x] PR 생성 후 diff·head·원격 CI 상태를 확인. 미검증 UI/Split 범위 명시.

## 2026-11-15 클라우드 진행

`42e13e8`에서 이어서 내부 증거 JSON 호환성과 이미지 연산 무결성 gate를 수정했고 독립 리뷰를 완료했다. DB 없는 전체 1064통과/0실패/63건너뜀, 전체 빌드 경고·오류0, web 설치·타입검사·빌드 및 PS7 스크립트23/23을 확인했다. 실제 SQL2025 CU10의 새 pipeline 4사례도 4/4통과했다. 전체 DB(SP7)1191통과/0실패/0건너뜀, 최종 기본30×3 공통103지표동일, EXEC37×3 종료0·상태불일치0, N1 42/42 일치까지 완료했다. 최종 orphan gate와 Linux 프로세스 정리 수정도 독립 리뷰 및 실제 회귀를 통과했다. 기존 Draft PR #1을 같은 브랜치로 갱신하며 신규 PR·merge·배포는 하지 않는다. 최종 스크립트 회귀는 실제 프로세스 종료를 추가한 24/24다. 근거: [클라우드 검증](../../test-reports/2026-11-15-cloud-validation.md).

실제 DB 상세와 남은 범위: [클라우드 DB 검증](../../test-reports/2026-11-15-cloud-db-validation.md). Split 원본 서버·SP11~21 수동 실험·브라우저 UX·대량 M5는 미검증 범위로 명시하며 Draft를 유지한다. 원격 HEAD/PR diff를 확인했고 자동 CI 체크는 등록되어 있지 않았다.
