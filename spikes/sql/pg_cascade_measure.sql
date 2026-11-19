\set ON_ERROR_STOP on

CREATE SCHEMA perf;
CREATE TABLE perf.parent (id integer PRIMARY KEY);
CREATE TABLE perf.child (
    id integer PRIMARY KEY,
    parent_id integer NOT NULL REFERENCES perf.parent(id) ON DELETE CASCADE
);
CREATE TABLE perf.grandchild (
    id integer PRIMARY KEY,
    child_id integer NOT NULL REFERENCES perf.child(id) ON DELETE CASCADE
);
CREATE INDEX child_parent_id_idx ON perf.child(parent_id);
CREATE INDEX grandchild_child_id_idx ON perf.grandchild(child_id);

INSERT INTO perf.parent SELECT g FROM generate_series(1, :parent_rows) AS g;
INSERT INTO perf.child SELECT g, (g + 1) / 2 FROM generate_series(1, 2 * :parent_rows) AS g;
INSERT INTO perf.grandchild SELECT id, id FROM perf.child;
ANALYZE;
