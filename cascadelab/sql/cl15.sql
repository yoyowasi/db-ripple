-- CL15: PK를 바꾸는 다중 행 UPDATE (E7). 트리거·FK 없음.
--   Seat: 바뀌지 않는 다른 유일 키가 없다 -> 짝 미확정(UNPAIRED)
--   Emp : badge가 NOT NULL UNIQUE이고 바뀌지 않는다 -> 그 키로 짝(BY_UNIQUE)
USE CascadeLab;
GO
CREATE SCHEMA cl15;
GO
CREATE TABLE cl15.Seat (
    id    int         NOT NULL CONSTRAINT PK_cl15_Seat PRIMARY KEY,
    label varchar(10) NOT NULL
);
CREATE TABLE cl15.Emp (
    id    int         NOT NULL CONSTRAINT PK_cl15_Emp PRIMARY KEY,
    badge varchar(10) NOT NULL CONSTRAINT UQ_cl15_Emp_badge UNIQUE,
    name  varchar(20) NOT NULL
);
GO
INSERT INTO cl15.Seat (id, label) VALUES (1, 's1'), (2, 's2'), (3, 's3');
INSERT INTO cl15.Emp (id, badge, name) VALUES (1, 'B001', 'e1'), (2, 'B002', 'e2'), (3, 'B003', 'e3');
GO
