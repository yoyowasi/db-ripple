-- CL19: xml·hierarchyid·geography·text 열 (형식 규칙, E16, §6.1). 트리거 없음.
-- text 열은 AFTER 트리거에서 참조할 수 없어(A12, SP6의 311) 감사 이미지에 없다. 수집 불가 열로 두고 최종 차이에서만 비교한다.
USE CascadeLab;
GO
CREATE SCHEMA cl19;
GO
CREATE TABLE cl19.Doc (
    id     int         NOT NULL CONSTRAINT PK_cl19_Doc PRIMARY KEY,
    body   xml         NULL,
    node   hierarchyid NULL,
    loc    geography   NULL,
    legacy text        NULL,
    note   varchar(20) NOT NULL
);
GO
INSERT INTO cl19.Doc (id, body, node, loc, legacy, note) VALUES
    (1, N'<a>1</a>', hierarchyid::Parse('/1/1/'), geography::Point(37.5, 127.0, 4326), 'old text', 'n1'),
    (2, N'<a>2</a>', hierarchyid::Parse('/1/2/'), geography::Point(35.1, 129.0, 4326), 'keep', 'n2');
GO
