SET NOCOUNT ON; SET XACT_ABORT ON;
BEGIN TRAN;
DECLARE @now datetime2 = SYSUTCDATETIME();
-- (a) The 11 floor-chart people (#192-202): now authorized by the Floor Management chart. Reinstate and reopen their floor roles.
UPDATE StaffMembers SET ArchivedAt=NULL, ArchiveReason=NULL, UpdatedAt=@now WHERE Id BETWEEN 192 AND 202 AND ArchivedAt IS NOT NULL;
PRINT CONCAT('reinstated staff: ', @@ROWCOUNT);
UPDATE FloorStaffAssignments SET IsCurrent=1, EndDate=NULL, EndReason=NULL, UpdatedAt=@now
 WHERE StaffMemberId BETWEEN 192 AND 202 AND IsCurrent=0 AND EndReason='Archived: not in the official 2026/2027 staff sources';
PRINT CONCAT('reopened floor assignments: ', @@ROWCOUNT);
-- (b) Sarah Mostafa: identity not confirmed (user decision). Remove the earlier seeder's link to Ms. Sara Mostafa (#136); flag it.
DELETE FROM FloorStaffAssignments WHERE Id=1 AND StaffMemberId=136 AND Role=1 AND SourceKey='floor-mgmt-2027:1:StageCoordinator:sarah-mostafa';
PRINT CONCAT('removed unconfirmed Sarah->Sara link: ', @@ROWCOUNT);
IF NOT EXISTS (SELECT 1 FROM StaffDataQualityFlags WHERE SourceKey='floor-mgmt-2027:identity:sarah-mostafa')
 INSERT StaffDataQualityFlags(SourceKey, StaffMemberId, Kind, Message, IsResolved, CreatedAt) VALUES ('floor-mgmt-2027:identity:sarah-mostafa', 136, 3,
 N'The Floor Management chart names Sarah Mostafa (201096247514) as Stage Coordinator of the Ground Floor (PreK – G1B). That is the same number as this record (01096247514), but she is not confirmed to be Ms. Sara Mostafa. The Ground Floor Stage Coordinator stays pending until an administrator confirms who she is.', 0, @now);
-- (c) Nour Tarek: the floor chart names his role (Stage Coordinator, 1st Floor). The unreadable-title flag is answered.
UPDATE StaffDataQualityFlags SET IsResolved=1, ResolvedAt=@now, ResolvedBy=N'Floor Management chart (2026/2027)' WHERE SourceKey='flag:pending:p-nour-tarek' AND IsResolved=0;
PRINT CONCAT('Nour Tarek flag resolved: ', @@ROWCOUNT);
UPDATE StaffMembers SET JobTitle=N'Stage Coordinator', UpdatedAt=@now WHERE Id=148 AND JobTitle IS NULL;
-- (d) Passant / Bassant Mamdouh: same phone, same surname; kept as one person, spelling flagged.
IF NOT EXISTS (SELECT 1 FROM StaffDataQualityFlags WHERE SourceKey='floor-mgmt-2027:identity:passant-mamdouh')
 INSERT StaffDataQualityFlags(SourceKey, StaffMemberId, Kind, Message, IsResolved, CreatedAt) VALUES ('floor-mgmt-2027:identity:passant-mamdouh', 130, 3,
 N'The Floor Management chart names "Passant Mamdouh" (201000300977) as Stage Coordinator of the 2nd Floor; the official staff document lists "Ms. Bassant Mamdouh" (01000300977) as Floor Supervisor. Same number and surname, so they are recorded as one person; confirm the spelling.', 0, @now);
COMMIT;
