-- CL04: ON UPDATE CASCADE (부모 PK 변경, 다중 행). 트리거 없음.
USE CascadeLab;
GO
CREATE SCHEMA cl04;
GO
CREATE TABLE cl04.P (
    id   int         NOT NULL CONSTRAINT PK_cl04_P PRIMARY KEY,
    name varchar(20) NOT NULL
);
CREATE TABLE cl04.Ch (
    id   int NOT NULL CONSTRAINT PK_cl04_Ch PRIMARY KEY,
    p_id int NOT NULL CONSTRAINT FK_cl04_Ch_P REFERENCES cl04.P (id) ON UPDATE CASCADE
);
GO
INSERT INTO cl04.P (id, name) VALUES (1, 'p1'), (2, 'p2'), (3, 'p3');
INSERT INTO cl04.Ch (id, p_id) VALUES (10, 1), (11, 1), (20, 2), (30, 3);
GO
