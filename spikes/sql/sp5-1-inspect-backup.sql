-- SP5-1: .bak 점검. master에서 실행. 경로·파일명 고정(restore-aw-origin.ps1과 동일).
RESTORE HEADERONLY FROM DISK = N'/var/opt/mssql/backup/AdventureWorks2025.bak';
GO
-- Type: D=데이터, L=로그, S=FILESTREAM 컨테이너, F=풀텍스트 카탈로그(구형).
-- 메모리 최적화 파일 그룹은 복원 뒤 sys.filegroups(type='FX')로 판정(sp5-2).
RESTORE FILELISTONLY FROM DISK = N'/var/opt/mssql/backup/AdventureWorks2025.bak';
GO
