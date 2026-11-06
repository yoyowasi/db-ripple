-- CL02: ON DELETE SET NULL. 트리거 없음.
USE CascadeLab;
GO
CREATE SCHEMA cl02;
GO
CREATE TABLE cl02.Dept (
    id   int         NOT NULL CONSTRAINT PK_cl02_Dept PRIMARY KEY,
    name varchar(20) NOT NULL
);
CREATE TABLE cl02.Emp (
    id      int         NOT NULL CONSTRAINT PK_cl02_Emp PRIMARY KEY,
    dept_id int         NULL CONSTRAINT FK_cl02_Emp_Dept REFERENCES cl02.Dept (id) ON DELETE SET NULL,
    name    varchar(20) NOT NULL
);
GO
INSERT INTO cl02.Dept (id, name) VALUES (1, 'd1'), (2, 'd2');
INSERT INTO cl02.Emp (id, dept_id, name) VALUES (10, 1, 'e10'), (11, 1, 'e11'), (20, 2, 'e20'), (30, NULL, 'e30');
GO
