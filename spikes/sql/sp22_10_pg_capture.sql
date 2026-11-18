-- SP22: PostgreSQL 18에서 감사 수집 방식 확인(미리보기 최소판의 전제).
-- 실행: docker exec -i dbripple-pg psql -U postgres -d sp22 -v ON_ERROR_STOP=1 -f - < spikes/sql/sp22_10_pg_capture.sql
-- 확인할 것
--   H1 문장 단위 AFTER 트리거 + 전이 테이블(REFERENCING)이 FK 연쇄로 바뀐 자식 행을 잡는가, 몇 번 발동하는가
--   H2 pg_trigger_depth(): 직접 문장 1, FK 연쇄·사용자 트리거 쓰기는 몇인가
--   H3 감사 기록 순서(seq): 부모 먼저(전위)인가 자식 먼저(후위)인가
--   H4 0행 문장·0행 연쇄에서도 문장 단위 트리거가 발동하는가
--   H5 한 트리거에 여러 사건(INSERT OR UPDATE OR DELETE)과 전이 테이블을 함께 둘 수 있는가
--   H6 FK 연쇄가 카탈로그에 내부 RI 트리거(tgisinternal, tgconstraint)로 보이는가
--   H7 ROLLBACK 전에 감사 행을 읽을 수 있고, 롤백 뒤에는 남지 않는가

\set QUIET on
SELECT version();

-- ---- 시험 스키마: 3단 연쇄 + SET NULL + 트리거 사슬 ----
DROP SCHEMA IF EXISTS s CASCADE;
CREATE SCHEMA s;
CREATE TABLE s.parent (id int PRIMARY KEY, name text);
CREATE TABLE s.child (id int PRIMARY KEY, parent_id int REFERENCES s.parent(id) ON DELETE CASCADE, v int);
CREATE TABLE s.grandchild (id int PRIMARY KEY, child_id int REFERENCES s.child(id) ON DELETE CASCADE);
CREATE TABLE s.note (id int PRIMARY KEY, parent_id int REFERENCES s.parent(id) ON DELETE SET NULL);
CREATE TABLE s.orders (id int PRIMARY KEY, total numeric(10,2), rev int DEFAULT 0);
CREATE TABLE s.order_lines (id int PRIMARY KEY, order_id int REFERENCES s.orders(id) ON DELETE CASCADE, qty int, price numeric(10,2));
CREATE TABLE s.history (id serial PRIMARY KEY, order_id int, at timestamptz DEFAULT now());

-- 사용자 트리거: 상세가 바뀌면 주문 합계를 다시 계산(행 단위), 주문이 바뀌면 이력 추가(행 단위)
CREATE FUNCTION s.recalc() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  UPDATE s.orders o SET total = (SELECT coalesce(sum(qty*price),0) FROM s.order_lines l WHERE l.order_id = o.id)
   WHERE o.id = coalesce(NEW.order_id, OLD.order_id);
  RETURN NULL;
END $$;
CREATE TRIGGER t_lines_recalc AFTER INSERT OR UPDATE OR DELETE ON s.order_lines FOR EACH ROW EXECUTE FUNCTION s.recalc();
CREATE FUNCTION s.hist() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  INSERT INTO s.history(order_id) VALUES (NEW.id);
  UPDATE s.orders SET rev = rev + 1 WHERE id = NEW.id AND pg_trigger_depth() < 3; -- 같은 테이블 다시 쓰기(깊이 제한)
  RETURN NULL;
END $$;
CREATE TRIGGER t_orders_hist AFTER UPDATE OF total ON s.orders FOR EACH ROW EXECUTE FUNCTION s.hist();

INSERT INTO s.parent VALUES (1,'p1'),(2,'p2'),(3,'p3-no-children');
INSERT INTO s.child VALUES (10,1,1),(11,1,2),(20,2,3);
INSERT INTO s.grandchild VALUES (100,10),(101,10),(110,11);
INSERT INTO s.note VALUES (1000,1),(1001,1),(1002,2);
INSERT INTO s.orders VALUES (7, 0, 0);
INSERT INTO s.order_lines VALUES (70,7,1,10),(71,7,2,5);
UPDATE s.orders SET total = 20, rev = 0;  -- 사용자 트리거가 이미 돈 상태를 정리
DELETE FROM s.history;

-- ---- 감사 저장소와 문장 단위 감사 트리거(사건별 3개, 전이 테이블) ----
CREATE TABLE s.dbr_audit (
  seq bigserial PRIMARY KEY,
  depth int, tbl text, op text, n_old int, n_new int,
  old_rows jsonb, new_rows jsonb, xid xid8 DEFAULT pg_current_xact_id(), at timestamptz DEFAULT clock_timestamp()
);
CREATE FUNCTION s.dbr_audit_ins() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  INSERT INTO s.dbr_audit(depth,tbl,op,n_old,n_new,old_rows,new_rows)
  SELECT pg_trigger_depth(), TG_TABLE_SCHEMA||'.'||TG_TABLE_NAME, TG_OP, 0, count(*), NULL, jsonb_agg(to_jsonb(n)) FROM dbr_new n;
  RETURN NULL;
END $$;
CREATE FUNCTION s.dbr_audit_upd() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  INSERT INTO s.dbr_audit(depth,tbl,op,n_old,n_new,old_rows,new_rows)
  SELECT pg_trigger_depth(), TG_TABLE_SCHEMA||'.'||TG_TABLE_NAME, TG_OP,
         (SELECT count(*) FROM dbr_old), (SELECT count(*) FROM dbr_new),
         (SELECT jsonb_agg(to_jsonb(o)) FROM dbr_old o), (SELECT jsonb_agg(to_jsonb(n)) FROM dbr_new n);
  RETURN NULL;
END $$;
CREATE FUNCTION s.dbr_audit_del() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  INSERT INTO s.dbr_audit(depth,tbl,op,n_old,n_new,old_rows,new_rows)
  SELECT pg_trigger_depth(), TG_TABLE_SCHEMA||'.'||TG_TABLE_NAME, TG_OP, count(*), 0, jsonb_agg(to_jsonb(o)), NULL FROM dbr_old o;
  RETURN NULL;
END $$;

DO $$
DECLARE t record;
BEGIN
  FOR t IN SELECT c.oid::regclass AS r, c.relname FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
           WHERE n.nspname='s' AND c.relkind='r' AND c.relname <> 'dbr_audit' LOOP
    EXECUTE format('CREATE TRIGGER zzz_dbr_i AFTER INSERT ON %s REFERENCING NEW TABLE AS dbr_new FOR EACH STATEMENT EXECUTE FUNCTION s.dbr_audit_ins()', t.r);
    EXECUTE format('CREATE TRIGGER zzz_dbr_u AFTER UPDATE ON %s REFERENCING OLD TABLE AS dbr_old NEW TABLE AS dbr_new FOR EACH STATEMENT EXECUTE FUNCTION s.dbr_audit_upd()', t.r);
    EXECUTE format('CREATE TRIGGER zzz_dbr_d AFTER DELETE ON %s REFERENCING OLD TABLE AS dbr_old FOR EACH STATEMENT EXECUTE FUNCTION s.dbr_audit_del()', t.r);
  END LOOP;
END $$;

\set QUIET off
\echo '== H5: 여러 사건 + 전이 테이블 한 트리거'
DO $$ BEGIN
  EXECUTE 'CREATE TRIGGER h5 AFTER INSERT OR UPDATE OR DELETE ON s.parent REFERENCING OLD TABLE AS o NEW TABLE AS n FOR EACH STATEMENT EXECUTE FUNCTION s.dbr_audit_upd()';
  RAISE NOTICE 'H5: created (multi-event transition allowed)';
  EXECUTE 'DROP TRIGGER h5 ON s.parent';
EXCEPTION WHEN others THEN RAISE NOTICE 'H5: error % %', SQLSTATE, SQLERRM;
END $$;

\echo '== H6: FK 연쇄의 내부 RI 트리거'
SELECT tgrelid::regclass AS on_table, tgname, tgisinternal, tgconstraint <> 0 AS has_constraint, pg_get_triggerdef(oid) LIKE '%RI_FKey_cascade_del%' AS cascade_del
  FROM pg_trigger WHERE tgrelid::regclass::text IN ('s.parent','s.child') AND tgisinternal ORDER BY 1,2;
SELECT conname, conrelid::regclass AS child, confrelid::regclass AS parent, confdeltype, confupdtype FROM pg_constraint WHERE contype='f' AND connamespace='s'::regnamespace ORDER BY 1;

\echo '== A: DELETE parent 1 (3단 연쇄 + SET NULL)'
BEGIN;
DELETE FROM s.parent WHERE id = 1;
SELECT seq, depth, tbl, op, n_old, n_new FROM s.dbr_audit ORDER BY seq;
ROLLBACK;
SELECT count(*) AS audit_after_rollback FROM s.dbr_audit;

\echo '== B: DELETE parent 3 (자식 없음: 0행 연쇄)'
BEGIN;
DELETE FROM s.parent WHERE id = 3;
SELECT seq, depth, tbl, op, n_old, n_new FROM s.dbr_audit ORDER BY seq;
ROLLBACK;

\echo '== C: DELETE parent 99 (0행 직접 문장)'
BEGIN;
DELETE FROM s.parent WHERE id = 99;
SELECT seq, depth, tbl, op, n_old, n_new FROM s.dbr_audit ORDER BY seq;
ROLLBACK;

\echo '== D: UPDATE order_lines 70 (사용자 트리거 사슬, 같은 테이블 다시 쓰기)'
BEGIN;
UPDATE s.order_lines SET qty = qty + 1 WHERE id = 70;
SELECT seq, depth, tbl, op, n_old, n_new, new_rows FROM s.dbr_audit ORDER BY seq;
ROLLBACK;

\echo '== E: DELETE parent 1,2 한 문장(부모 2행 -> 연쇄 문장이 부모 행마다 도는가)'
BEGIN;
DELETE FROM s.parent WHERE id IN (1,2);
SELECT seq, depth, tbl, op, n_old, n_new FROM s.dbr_audit ORDER BY seq;
ROLLBACK;
