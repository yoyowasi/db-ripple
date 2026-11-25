# PostgreSQL 18 측정 로그 요약

- GitHub Actions PostgreSQL 잡의 측정 단계는 2026-11-25 08:56:21~08:59:26 KST에 실행돼 종료 코드 0으로 끝났다. [잡 로그](https://github.com/yoyowasi/db-ripple/actions/runs/45514031243/job/138868719220)
- 세 크기의 기준선은 모두 `ready`였고 감사 대상은 각각 3개 테이블이었다. 각 크기의 미리보기 6회(워밍업 포함)는 모두 HTTP 200, 이어진 취소 요청은 모두 HTTP 204였다.
- 원본과 샌드박스의 세 업무 테이블은 측정 전후 행 수와 정렬한 열 값의 MD5가 같았다. 샌드박스 감사 헤더와 행 이미지도 측정 후 모두 0행이었다. 이 42개 확인값은 11-19의 행 수·MD5와 일치했다. [DB 확인 CSV](pg-db-checks.csv)
- 크기별 Host 로그에 heartbeat 지연, 경고, 오류 기록은 없었다. 스크립트는 PostgreSQL 컨테이너를 정지·제거했고 비밀번호 검색 결과 0건을 기록했다.
- `pg-samples.csv`의 `checks_passed`는 11-19와 같은 행 단위 FK 매칭·`certain`까지 요구한다. 5만·10만 부모 행의 `false`는 자식·손자 귀속이 `inferred`이고 확인된 행별 매칭 건수가 0이었던 표본이다. 두 FK 링크와 순 삭제 건수는 기대와 일치했다. [표본 CSV](pg-samples.csv)
