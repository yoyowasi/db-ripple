-- CL14: 힙 테이블(PK·유일 인덱스 없음, 중복 행) (ROW 키, 다중집합, E6). 트리거 없음.
-- 같은 행 ('a', 1)이 두 개 있어 행 하나를 키로 가리킬 수 없다. 최종 차이는 모든 열 묶음의 개수로 비교한다.
USE CascadeLab;
GO
CREATE SCHEMA cl14;
GO
CREATE TABLE cl14.Tag (
    name varchar(20) NOT NULL,
    qty  int         NOT NULL
);
GO
INSERT INTO cl14.Tag (name, qty) VALUES ('a', 1), ('a', 1), ('b', 2), ('c', 3);
GO
