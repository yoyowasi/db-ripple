# db-ripple

INSERT·UPDATE·DELETE를 실제 DB에 반영하기 전에 복제 DB에서 먼저 실행해 보고, FK 연쇄와 트리거까지 포함해 어느 테이블의 어떤 행이 바뀌는지 보여 주는 SQL Server용 웹 도구다. 변경이 어떤 FK·트리거 때문에 어떤 순서로 퍼졌는지는 그래프로, 행 값은 before/after 표로 보여 주고, 사용자가 [적용] 또는 [취소]를 고른다. 결과는 LLM 추론이 아니라 실제 실행에서 나온다.

- 상태: 설계 v1.2 완료(2026-11-06), 구현 전
- 설계 문서: [docs/architecture.md](docs/architecture.md)
- 스택: SQL Server 2025 · C# ASP.NET Core(.NET 10) · Vue 3
- 데이터: 공개 샘플 DB(AdventureWorks, pubs)와 직접 만드는 CascadeLab만 쓴다.
