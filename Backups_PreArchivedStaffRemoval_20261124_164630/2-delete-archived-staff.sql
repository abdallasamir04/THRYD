SET NOCOUNT ON; SET XACT_ABORT ON;
-- The 70 records archived by the official staff reconciliation that no authorized source supports (demo/test staff #1-70).
CREATE TABLE #D(Id int PRIMARY KEY);
INSERT #D SELECT Id FROM StaffMembers WHERE ArchivedAt IS NOT NULL;
IF (SELECT COUNT(*) FROM #D) <> 70 OR EXISTS (SELECT 1 FROM #D WHERE Id > 70) THROW 50001, 'Unexpected archived set: expected exactly #1-70.', 1;
IF EXISTS (SELECT 1 FROM TeachingRosterEntries WHERE StaffMemberId IN (SELECT Id FROM #D))
 OR EXISTS (SELECT 1 FROM StaffPositionAssignments WHERE StaffMemberId IN (SELECT Id FROM #D) AND SourceKey LIKE 'asg:%')
 OR EXISTS (SELECT 1 FROM FloorStaffAssignments WHERE StaffMemberId IN (SELECT Id FROM #D) AND IsCurrent=1)
 THROW 50002, 'An archived record is linked to an authorized source; nothing deleted.', 1;

CREATE TABLE #L(Id int PRIMARY KEY); INSERT #L SELECT Id FROM TimetableAssignments WHERE TeacherId IN (SELECT Id FROM #D);
CREATE TABLE #A(Id int PRIMARY KEY); INSERT #A SELECT Id FROM DutyAssignments WHERE PrimaryStaffId IN (SELECT Id FROM #D);
CREATE TABLE #S(Id int PRIMARY KEY); INSERT #S SELECT Id FROM DutyStaffAssignments WHERE StaffMemberId IN (SELECT Id FROM #D) OR DutyAssignmentId IN (SELECT Id FROM #A);
CREATE TABLE #T(Id int PRIMARY KEY); INSERT #T SELECT Id FROM ManagementTasks WHERE AssignedToId IN (SELECT Id FROM #D);
CREATE TABLE #I(Id int PRIMARY KEY); INSERT #I SELECT Id FROM FloorAreaInspections WHERE InspectorStaffId IN (SELECT Id FROM #D);
CREATE TABLE #M(Id int PRIMARY KEY); INSERT #M SELECT Id FROM StudentMovements WHERE RecordedByStaffId IN (SELECT Id FROM #D) OR ReturnedByStaffId IN (SELECT Id FROM #D);
-- Floor Audit log rows about the deleted demo inspections / movements and the demo inspectors access grants
CREATE TABLE #G(Id int PRIMARY KEY);
INSERT #G SELECT l.Id FROM FloorAuditLog l WHERE (l.Entity='Inspection' AND l.EntityId IN (SELECT Id FROM #I)) OR (l.Entity='Movement' AND l.EntityId IN (SELECT Id FROM #M))
 OR EXISTS (SELECT 1 FROM StaffMembers s WHERE s.Id IN (SELECT Id FROM #D) AND l.Text LIKE s.FullName + N': Inspector of %');

CREATE TABLE #C(tbl sysname, before int, after int NULL);
INSERT #C(tbl, before) SELECT t.name, SUM(p.rows) FROM sys.tables t JOIN sys.partitions p ON p.object_id=t.object_id AND p.index_id IN (0,1) GROUP BY t.name;

BEGIN TRAN;
-- Timetable lessons of the demo teachers (all demo-generated) and their dependants
DELETE FROM SessionCancellations WHERE TimetableAssignmentId IN (SELECT Id FROM #L);
UPDATE AcademicMaterialRecords SET TimetableAssignmentId=NULL WHERE TimetableAssignmentId IN (SELECT Id FROM #L);
UPDATE AttendanceAlerts SET TimetableAssignmentId=NULL WHERE TimetableAssignmentId IN (SELECT Id FROM #L);
UPDATE ActivitySessionCoverage SET TimetableAssignmentId=NULL WHERE TimetableAssignmentId IN (SELECT Id FROM #L);
DELETE FROM PeEquipmentItems WHERE TimetableAssignmentId IN (SELECT Id FROM #L);
DELETE FROM PeSessionDetails WHERE TimetableAssignmentId IN (SELECT Id FROM #L);
UPDATE PeSessionDetails SET CoTeacherId=NULL WHERE CoTeacherId IN (SELECT Id FROM #D);
DELETE FROM TimetableAssignments WHERE Id IN (SELECT Id FROM #L);
UPDATE AcademicMaterialRecords SET TeacherId=NULL, TeacherName=NULL WHERE TeacherId IN (SELECT Id FROM #D);
-- Workload
DELETE FROM WorkloadAssignmentHistory WHERE TeacherId IN (SELECT Id FROM #D);
DELETE FROM TeacherWorkloadAssignments WHERE TeacherId IN (SELECT Id FROM #D);
UPDATE TeacherWorkloadAssignments SET DutyAssignmentId=NULL WHERE DutyAssignmentId IN (SELECT Id FROM #A);
-- Duties of the demo staff (all demo-generated) with their audit trail and feedback
DELETE FROM StaffDutyFeedback WHERE EvaluatorStaffId IN (SELECT Id FROM #D) OR DutyStaffAssignmentId IN (SELECT Id FROM #S);
DELETE FROM MorningRevisionRecords WHERE TeacherId IN (SELECT Id FROM #D) OR DutyAssignmentId IN (SELECT Id FROM #A);
DELETE FROM DutyAudits WHERE DutyAssignmentId IN (SELECT Id FROM #A);
DELETE FROM DutyBackupResponses WHERE StaffMemberId IN (SELECT Id FROM #D) OR DutyAssignmentId IN (SELECT Id FROM #A);
DELETE FROM DutyStaffAssignments WHERE Id IN (SELECT Id FROM #S);
DELETE FROM DutyAssignmentClasses WHERE DutyAssignmentId IN (SELECT Id FROM #A);
UPDATE DutyAssignments SET BackupStaffId=NULL WHERE BackupStaffId IN (SELECT Id FROM #D) AND Id NOT IN (SELECT Id FROM #A);
DELETE FROM DutyAssignments WHERE Id IN (SELECT Id FROM #A);
-- Management tasks assigned to demo staff (all demo-generated) and their history
DELETE FROM ManagementTaskHistory WHERE TaskId IN (SELECT Id FROM #T) OR StaffMemberId IN (SELECT Id FROM #D);
DELETE FROM TaskAttachments WHERE TaskId IN (SELECT Id FROM #T);
DELETE FROM ManagementTasks WHERE Id IN (SELECT Id FROM #T);
UPDATE ManagementTasks SET CreatedById=NULL WHERE CreatedById IN (SELECT Id FROM #D);
UPDATE ManagementTasks SET ReviewerId=NULL WHERE ReviewerId IN (SELECT Id FROM #D);
-- Staff attendance of the demo staff
DELETE FROM StaffAttendanceCorrections WHERE StaffAttendanceRecordId IN (SELECT Id FROM StaffAttendanceRecords WHERE StaffMemberId IN (SELECT Id FROM #D));
DELETE FROM StaffAttendanceRecords WHERE StaffMemberId IN (SELECT Id FROM #D);
-- Roles held earlier (ended by the reconciliation) and their histories
DELETE FROM ClassTeachingHistory WHERE StaffMemberId IN (SELECT Id FROM #D);
DELETE FROM ClassTeachingAssignments WHERE StaffMemberId IN (SELECT Id FROM #D);
DELETE FROM DepartmentHeadAssignments WHERE TeacherId IN (SELECT Id FROM #D);
DELETE FROM ManagementPositionAssignments WHERE ManagementMemberId IN (SELECT Id FROM #D);
DELETE FROM StaffPositionAssignments WHERE StaffMemberId IN (SELECT Id FROM #D);
UPDATE StaffPositionAssignments SET ReportsToStaffMemberId=NULL WHERE ReportsToStaffMemberId IN (SELECT Id FROM #D);
DELETE FROM FloorStaffAssignments WHERE StaffMemberId IN (SELECT Id FROM #D);
DELETE FROM ActivityStaff WHERE StaffMemberId IN (SELECT Id FROM #D);
DELETE FROM StaffDataQualityFlags WHERE StaffMemberId IN (SELECT Id FROM #D);
-- Floor Audit: demo inspectors access, their [TEST] inspection and movement, and the log rows about them
DELETE FROM FloorAuditAccess WHERE StaffMemberId IN (SELECT Id FROM #D);
DELETE FROM FloorAuditLog WHERE Id IN (SELECT Id FROM #G);
UPDATE FloorAuditLog SET ByStaffId=NULL WHERE ByStaffId IN (SELECT Id FROM #D);
DELETE FROM FloorInspectionAttachments WHERE InspectionId IN (SELECT Id FROM #I);
DELETE FROM FloorInspectionResponses WHERE InspectionId IN (SELECT Id FROM #I);
DELETE FROM FloorAreaInspections WHERE Id IN (SELECT Id FROM #I);
DELETE FROM StudentMovements WHERE Id IN (SELECT Id FROM #M);
-- Optional person links on otherwise legitimate records: detached
UPDATE Incidents SET PreparedByStaffId=NULL WHERE PreparedByStaffId IN (SELECT Id FROM #D);
UPDATE Incidents SET ReviewedByStaffId=NULL WHERE ReviewedByStaffId IN (SELECT Id FROM #D);
UPDATE FloorReports SET CreatedByStaffId=NULL WHERE CreatedByStaffId IN (SELECT Id FROM #D);
UPDATE FloorReports SET RecipientStaffId=NULL WHERE RecipientStaffId IN (SELECT Id FROM #D);
UPDATE FloorReports SET RelatedStaffId=NULL WHERE RelatedStaffId IN (SELECT Id FROM #D);
UPDATE FloorTickets SET AssignedSocialWorkerId=NULL WHERE AssignedSocialWorkerId IN (SELECT Id FROM #D);
UPDATE FloorTickets SET CreatedByStaffId=NULL WHERE CreatedByStaffId IN (SELECT Id FROM #D);
UPDATE StudentArrivalRecords SET ReceivedByStaffId=NULL WHERE ReceivedByStaffId IN (SELECT Id FROM #D);
UPDATE TeachingRosterEntries SET StaffMemberId=NULL WHERE StaffMemberId IN (SELECT Id FROM #D);
-- Hard-coded demo names left on kept records
UPDATE SchoolHolidays SET CreatedBy=NULL WHERE CreatedBy IN (SELECT FullName FROM StaffMembers WHERE Id IN (SELECT Id FROM #D));
-- The people themselves
DELETE FROM StaffWorkingDays WHERE StaffMemberId IN (SELECT Id FROM #D);
DELETE FROM StaffMembers WHERE Id IN (SELECT Id FROM #D);
PRINT CONCAT('staff deleted: ', @@ROWCOUNT);
COMMIT;

UPDATE c SET after = x.n FROM #C c JOIN (SELECT t.name, SUM(p.rows) n FROM sys.tables t JOIN sys.partitions p ON p.object_id=t.object_id AND p.index_id IN (0,1) GROUP BY t.name) x ON x.name=c.tbl;
SELECT tbl, before, after, after-before diff FROM #C WHERE before<>after ORDER BY tbl;
