# 2026-11-19 클라우드 Split EXEC [적용] 재시도

**미실행(클라우드 한정).** SQL Server 2022와 2025의 이미지 설정 파일이 모두 `westus.data.mcr.microsoft.com`으로 리디렉션됐다. 해당 호스트에 대한 프록시 CONNECT가 두 요청에서 HTTP 403을 반환했다. 같은 날 앞선 실행의 이미지 다운로드 차단을 재현했다. [이전 작업 A](2026-11-19-codex-cloud.md#a-split-모드-exec-적용) · [이번 HTTP 기록](data/2026-11-19-codex-split-exec/registry-http.csv)

## 환경

`main` 커밋 `bbd8105e1cb24e4f0dc6f956d7aa125657bf1d49`에서 실행했다. Debian 13 x86_64, Linux 6.18.57, .NET SDK 10.0.404, PowerShell 7.5.5, Docker 29.9.2(VFS)였다. `nproc`은 3, cgroup CPU 할당량은 2개였다. `free -m`의 총 메모리는 9,968 MiB, cgroup 메모리 한도는 8 GiB, 시작 시 파일시스템 가용 공간은 56 GiB였다. `/workspace/.setup/env.sh`를 사용했다. [환경 로그](data/2026-11-19-codex-split-exec/environment.log)

## 확인한 사실

저장소 루트에서 기존 `.env`를 보존하고 Compose 서비스별 이미지 다운로드를 한 번씩 시도했다.

```bash
source /workspace/.setup/env.sh
umask 022
timeout 180s docker compose --profile origin pull sql
timeout 180s docker compose --profile origin pull sql-origin
```

두 명령은 `error pulling image configuration … Forbidden`을 남기고 종료 코드 1을 반환했다. 별도 curl GET으로 `mcr.microsoft.com`의 manifest에서 설정 파일 digest를 읽고, 설정 파일 요청의 리디렉션을 따라가 실패 지점을 확인했다. [샌드박스 Docker 로그](data/2026-11-19-codex-split-exec/sql-pull.log) · [원본 Docker 로그](data/2026-11-19-codex-split-exec/sql-origin-pull.log)

| 서비스 | 이미지 태그 | manifest GET | 설정 파일 GET | 리디렉션 호스트 | 프록시 CONNECT |
|---|---|---:|---:|---|---:|
| 샌드박스 `sql` | `2025-CU10-ubuntu-24.04` | 200 | 307 | `westus.data.mcr.microsoft.com` | 403 |
| 원본 `sql-origin` | `2022-latest` | 200 | 307 | `westus.data.mcr.microsoft.com` | 403 |

두 요청의 curl 종료 코드는 56이고, 리디렉션 대상의 HTTP 응답 코드는 `000`으로 기록됐다. 실행 중인 제한 정책에는 `centralus.data.mcr.microsoft.com`이 허용됐고, 실제 리디렉션된 `westus.data.mcr.microsoft.com`은 허용 목록에 없었다. [HTTP 기록](data/2026-11-19-codex-split-exec/registry-http.csv) · [네트워크 정책](data/2026-11-19-codex-split-exec/network-policy.json)

## 확인하지 못한 범위

Compose에 등록된 두 서비스의 컨테이너는 없었다. 이미지 다운로드에서 중단했으므로 SQL Server 기동, DB 지문, EXEC 미리보기 및 [적용]은 미확인이다. 제품 동작에 대한 판정은 보류한다. [컨테이너 목록](data/2026-11-19-codex-split-exec/compose-ps.json)
