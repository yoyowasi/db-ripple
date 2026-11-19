#!/usr/bin/env bash
set -euo pipefail
umask 022

cd "$(dirname "${BASH_SOURCE[0]}")/.."
out=$(realpath -m "${1:?새 로그 디렉터리를 지정하세요}")
mkdir "$out"
host_dir=$PWD/src/DbRipple.Host
container="dbripple-pg-cascade-$$"
pg_port=${DBRIPPLE_MEASURE_PG_PORT:-55432}
base=http://127.0.0.1:${DBRIPPLE_MEASURE_HOST_PORT:-15080}
host_pid=
container_id=
export POSTGRES_PASSWORD="Aa1-$(openssl rand -hex 16)"

stop_host() {
    if [[ -n $host_pid ]]; then
        kill "$host_pid" 2>/dev/null || true
        wait "$host_pid" 2>/dev/null || true
        host_pid=
    fi
}

cleanup() {
    local result=$?
    stop_host
    if [[ -n $container_id ]]; then
        docker stop "$container_id" >> "$out/cleanup.log" 2>&1 || result=1
        docker rm "$container_id" >> "$out/cleanup.log" 2>&1 || result=1
    fi
    local scan_status=0
    grep -rF -- "$POSTGRES_PASSWORD" "$out" >/dev/null || scan_status=$?
    case $scan_status in
        1) printf 'PostgreSQL 비밀번호 검색 결과 0건\n' > "$out/secret-scan.log" ;;
        *) printf 'PostgreSQL 비밀번호 검색 확인 실패\n' >&2; result=1 ;;
    esac
    rm -rf "$out/store"
    exit "$result"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

container_id=$(docker run -d --name "$container" --publish "127.0.0.1:$pg_port:5432" \
    --env POSTGRES_PASSWORD postgres:18)
pg() { docker exec -i "$container" psql -X -U postgres -v ON_ERROR_STOP=1 "$@"; }

ready=false
for ((attempt=0; attempt<60; attempt++)); do
    if docker exec "$container" pg_isready -h 127.0.0.1 -U postgres >/dev/null 2>&1; then
        ready=true
        break
    fi
    sleep 1
done
if [[ $ready != true ]]; then
    docker logs "$container" > "$out/postgres-startup.log" 2>&1
    exit 1
fi
docker image inspect --format '{{index .RepoDigests 0}}' postgres:18 > "$out/postgres-image.log"
pg -d postgres -At -c 'SELECT version()' > "$out/postgres-version.log"

sql_counts() {
    local database=$1
    pg -d "$database" --csv -c "
        SELECT 'child' AS table_name, count(*) AS rows, md5(string_agg(id || ':' || parent_id, ',' ORDER BY id)) AS fingerprint FROM perf.child
        UNION ALL SELECT 'grandchild', count(*), md5(string_agg(id || ':' || child_id, ',' ORDER BY id)) FROM perf.grandchild
        UNION ALL SELECT 'parent', count(*), md5(string_agg(id::text, ',' ORDER BY id)) FROM perf.parent
        ORDER BY table_name"
}

post() {
    curl --silent --show-error --max-time 180 --output "$1" --write-out '%{http_code},%{time_total},%{size_download}\n' \
        --request POST --header "Origin: $base" --header 'X-DbRipple-Request: 1' \
        --header 'Content-Type: application/json' --data "$2" "$base/$3"
}

baseline() {
    local dir=$1
    for ((attempt=0; attempt<60; attempt++)); do
        if curl -fsS --max-time 2 "$base/api/sandbox" -o "$dir/sandbox-initial.json" 2>/dev/null; then
            break
        fi
        sleep 1
    done
    local http
    http=$(post "$dir/refresh.json" '{}' api/sandbox/refresh)
    printf '%s\n' "$http" > "$dir/refresh-http.csv"
    [[ $http == 202,* ]] || return 1
    for ((attempt=0; attempt<120; attempt++)); do
        curl -fsS --max-time 5 "$base/api/sandbox" -o "$dir/sandbox-final.json"
        case $(jq -r .state "$dir/sandbox-final.json") in
            ready) return 0 ;;
            error) return 1 ;;
        esac
        sleep 1
    done
    return 1
}

printf 'parent_rows,run,http_status,http_seconds,response_bytes\n' > "$out/http.csv"
result=0
for n in 10000 50000 100000; do
    dir="$out/n$n"
    mkdir "$dir"
    origin="cascade_${n}"
    sandbox="${origin}_dbr_sandbox"
    pg -d postgres -c "CREATE DATABASE $origin" > "$dir/seed.log"
    pg -d "$origin" -v parent_rows="$n" -f - < spikes/sql/pg_cascade_measure.sql >> "$dir/seed.log" 2>&1

    (
        cd "$host_dir"
        export DBRIPPLE_ENGINE=postgres
        export DBRIPPLE_PG_CONNECTION="Host=127.0.0.1;Port=$pg_port;Username=postgres;Password=$POSTGRES_PASSWORD;Database=postgres"
        export DbRipple__Pg__OriginDb="$origin" DbRipple__Urls="$base" DbRipple__PreviewStoreDir="$out/store"
        exec dotnet bin/Debug/net10.0/DbRipple.Host.dll --dev --no-browser
    ) > "$dir/host.log" 2>&1 &
    host_pid=$!
    if ! baseline "$dir"; then
        stop_host
        result=1
        continue
    fi
    sql_counts "$origin" > "$dir/origin-before.csv"
    sql_counts "$sandbox" > "$dir/sandbox-before.csv"
    cmp "$dir/origin-before.csv" "$dir/sandbox-before.csv"

    for run in warmup 1 2 3 4 5; do
        body='{"sql":"DELETE FROM perf.parent WHERE id >= 1;"}'
        json="$dir/preview-$run.json"
        if http=$(post "$json" "$body" api/previews); then
            printf '%s,%s,%s\n' "$n" "$run" "$http" >> "$out/http.csv"
        else
            printf '%s,%s,%s\n' "$n" "$run" "$http" >> "$out/http.csv"
            result=1
            break
        fi
        if [[ $http != 200,* ]] || ! jq -e '.status == "ok" or .status == "truncated"' "$json" >/dev/null; then
            result=1
            break
        fi
        id=$(jq -er .previewId "$json")
        canceled=$(post "$dir/cancel-$run.json" '{}' "api/previews/$id/cancel")
        [[ $canceled == 204,* ]]
    done

    sql_counts "$origin" > "$dir/origin-after.csv"
    sql_counts "$sandbox" > "$dir/sandbox-after.csv"
    pg -d "$sandbox" --csv -c 'SELECT (SELECT count(*) FROM dbr_audit.header) AS header, (SELECT count(*) FROM dbr_audit.row_image) AS row_image' > "$dir/audit-after.csv"
    cmp "$dir/origin-before.csv" "$dir/origin-after.csv"
    cmp "$dir/sandbox-before.csv" "$dir/sandbox-after.csv"
    stop_host
done
exit "$result"
