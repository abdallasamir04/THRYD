using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Data;

public class SchoolDbContext(DbContextOptions<SchoolDbContext> options) : DbContext(options)
{
    public DbSet<SchoolClass> Classes => Set<SchoolClass>();
    public DbSet<Student> Students => Set<Student>();
    public DbSet<StudentTransferHistory> StudentTransfers => Set<StudentTransferHistory>();
    public DbSet<StaffMember> StaffMembers => Set<StaffMember>();
    public DbSet<Teacher> Teachers => Set<Teacher>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<AppSetting> Settings => Set<AppSetting>();
    public DbSet<DutyAssignment> DutyAssignments => Set<DutyAssignment>();
    public DbSet<DutyAssignmentClass> DutyAssignmentClasses => Set<DutyAssignmentClass>();
    public DbSet<DutyBackupResponse> DutyBackupResponses => Set<DutyBackupResponse>();
    public DbSet<TeacherWorkloadAssignment> WorkloadAssignments => Set<TeacherWorkloadAssignment>();
    public DbSet<WorkloadAssignmentHistory> WorkloadHistory => Set<WorkloadAssignmentHistory>();
    public DbSet<DutyAudit> DutyAudits => Set<DutyAudit>();
    public DbSet<DutyStaffAssignment> DutyStaffAssignments => Set<DutyStaffAssignment>();
    public DbSet<StaffDutyFeedback> StaffFeedback => Set<StaffDutyFeedback>();
    public DbSet<StudentArrivalRecord> StudentArrivals => Set<StudentArrivalRecord>();
    public DbSet<StudentArrivalCorrection> StudentArrivalCorrections => Set<StudentArrivalCorrection>();
    public DbSet<DepartmentHeadAssignment> DepartmentHeads => Set<DepartmentHeadAssignment>();
    public DbSet<ManagementMember> ManagementMembers => Set<ManagementMember>();
    public DbSet<ManagementTask> ManagementTasks => Set<ManagementTask>();
    public DbSet<ManagementTaskHistory> ManagementTaskHistory => Set<ManagementTaskHistory>();
    public DbSet<DepartmentSubcategory> DepartmentSubcategories => Set<DepartmentSubcategory>();
    public DbSet<ManagementPositionAssignment> PositionAssignments => Set<ManagementPositionAssignment>();
    public DbSet<TimeSlot> TimeSlots => Set<TimeSlot>();
    public DbSet<TimetableAssignment> TimetableAssignments => Set<TimetableAssignment>();
    public DbSet<PeSessionDetail> PeSessionDetails => Set<PeSessionDetail>();
    public DbSet<PeEquipmentItem> PeEquipmentItems => Set<PeEquipmentItem>();
    public DbSet<AcademicYear> AcademicYears => Set<AcademicYear>();
    public DbSet<AcademicQuarter> AcademicQuarters => Set<AcademicQuarter>();
    public DbSet<BelongingsRecord> BelongingsRecords => Set<BelongingsRecord>();
    public DbSet<AcademicMaterialRecord> AcademicMaterialRecords => Set<AcademicMaterialRecord>();
    public DbSet<BelongingsNoteHistory> BelongingsNoteHistory => Set<BelongingsNoteHistory>();

    /// <summary>Backstop: a new or changed timetable session may not take a teacher above 24 in any week it counts.</summary>
    public const string TimetableLimitTrigger = "TR_TimetableAssignments_MaxWeeklyPeriods";

    private static readonly DateTime SeedTime = new(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Discriminator values of the StaffMembers table.</summary>
    public const string TeacherType = "Teacher";
    public const string ManagementType = "Management";

    /// <summary>Database-level backstop for the 24-period limit (see the DutyAndWorkload migration).</summary>
    public const string WorkloadLimitTrigger = "TR_TeacherWorkloadAssignments_MaxWeeklyPeriods";

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<SchoolClass>(e =>
        {
            e.ToTable("Classes");
            e.Property(x => x.Name).HasMaxLength(SchoolClass.NameMaxLength).IsRequired();
            e.Property(x => x.NormalizedName).HasMaxLength(SchoolClass.NameMaxLength).IsRequired();
            e.HasIndex(x => x.NormalizedName).IsUnique();
            e.HasIndex(x => x.SortOrder);
            e.Property(x => x.Capacity).HasDefaultValue(25);
            e.Property(x => x.IsActive).HasDefaultValue(true);
            e.HasData(DefaultClasses.Seed());
        });

        b.Entity<Student>(e =>
        {
            e.ToTable("Students");
            e.Property(x => x.FullName).HasMaxLength(Student.NameMaxLength).IsRequired();
            e.Property(x => x.NormalizedName).HasMaxLength(Student.NameMaxLength).IsRequired();
            // A student is never deleted with their class: the class delete is blocked or reassigns first.
            e.HasOne(x => x.SchoolClass).WithMany(c => c.Students)
                .HasForeignKey(x => x.SchoolClassId).OnDelete(DeleteBehavior.Restrict);
            // The same name may exist in two classes, but not twice in one class.
            e.HasIndex(x => new { x.SchoolClassId, x.NormalizedName }).IsUnique();
            e.HasIndex(x => x.NormalizedName);
            e.HasIndex(x => x.CreatedAt);
        });

        b.Entity<StaffMember>(e =>
        {
            // Management Team members are never members of an academic department.
            e.ToTable("StaffMembers", t => t.HasCheckConstraint("CK_StaffMembers_ManagementHasNoDepartment",
                $"[StaffType] <> '{ManagementType}' OR ([DepartmentId] IS NULL AND [Subcategory] IS NULL AND [Subject] IS NULL)"));
            e.HasDiscriminator<string>("StaffType").HasValue<Teacher>(TeacherType).HasValue<ManagementMember>(ManagementType);
            e.Property("StaffType").HasMaxLength(30);
            e.Property(x => x.Subject).HasMaxLength(50);
            e.Property(x => x.FullName).HasMaxLength(StaffMember.NameMaxLength).IsRequired();
            e.Property(x => x.NormalizedName).HasMaxLength(StaffMember.NameMaxLength).IsRequired();
            e.Property(x => x.JobTitle).HasMaxLength(StaffMember.JobTitleMaxLength);
            e.Property(x => x.ContactNumber).HasMaxLength(StaffMember.ContactMaxLength).IsUnicode(false);
            e.Property(x => x.NormalizedContact).HasMaxLength(StaffMember.ContactMaxLength).IsUnicode(false).HasDefaultValue("");
            e.Property(x => x.Subcategory).HasMaxLength(50);
            e.HasOne(x => x.Department).WithMany(d => d.StaffMembers).HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.SetNull);
            // Backstop for duplicate prevention (the service also treats a missing number as a match).
            e.HasIndex("StaffType", nameof(StaffMember.NormalizedName), nameof(StaffMember.NormalizedContact)).IsUnique();
            e.HasIndex(x => x.DepartmentId);
            e.HasIndex(x => x.JobTitle);
            e.HasIndex(x => x.CreatedAt);
        });

        b.Entity<Department>(e =>
        {
            e.ToTable("Departments");
            e.Property(x => x.Name).HasMaxLength(Department.NameMaxLength).IsRequired();
            e.Property(x => x.NormalizedName).HasMaxLength(Department.NameMaxLength).IsRequired();
            e.Property(x => x.Subcategories).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(Department.DescriptionMaxLength);
            e.Property(x => x.SubcategoryLabel).HasMaxLength(50);
            e.Property(x => x.SubjectLabel).HasMaxLength(50);
            e.HasIndex(x => x.NormalizedName).IsUnique();
            // Rows 12–15 are inserted by the StaffStructure migration only when no department with the same name exists;
            // row 12 ("Reading Club") is renamed "Library / Reading" by the StaffTimetable migration.
            e.HasData(
                new Department { Id = 1, Name = "Arabic", NormalizedName = "ARABIC", SortOrder = 1 },
                new Department { Id = 2, Name = "English", NormalizedName = "ENGLISH", SortOrder = 2 },
                new Department { Id = 3, Name = "Math", NormalizedName = "MATH", SortOrder = 3 },
                new Department { Id = 4, Name = "Science", NormalizedName = "SCIENCE", SortOrder = 4 },
                new Department { Id = 5, Name = "ASS", NormalizedName = "ASS", SortOrder = 5 },
                new Department { Id = 6, Name = "ESS", NormalizedName = "ESS", SortOrder = 6 },
                new Department { Id = 7, Name = "Religion", NormalizedName = "RELIGION", Subcategories = "Islamic,Christian", SubcategoryLabel = "Religion type", SubjectLabel = "Subject", SortOrder = 7 },
                new Department { Id = 8, Name = "PE", NormalizedName = "PE", SortOrder = 8 },
                new Department { Id = 9, Name = "Art", NormalizedName = "ART", SortOrder = 9 },
                new Department { Id = 10, Name = "ICT", NormalizedName = "ICT", Subcategories = "ICT,AI", SubcategoryLabel = "Specialisation", SortOrder = 10 },
                new Department { Id = 11, Name = "Music", NormalizedName = "MUSIC", SortOrder = 11 },
                new Department { Id = 12, Name = "Library / Reading", NormalizedName = "LIBRARY / READING", SortOrder = 12 },
                new Department { Id = 13, Name = "Second Language", NormalizedName = "SECOND LANGUAGE", Subcategories = "French,Deutsch", SubcategoryLabel = "Language", SortOrder = 13 },
                new Department { Id = 14, Name = "Drama", NormalizedName = "DRAMA", SortOrder = 14 },
                new Department { Id = 15, Name = "WSD", NormalizedName = "WSD", Description = "Wellbeing, Support & Development", Subcategories = "Wellbeing,Support,Discipline,Character Building", SubcategoryLabel = "Subdepartment", SortOrder = 15 }
            );
        });

        b.Entity<DepartmentSubcategory>(e =>
        {
            e.ToTable("DepartmentSubcategories");
            e.Property(x => x.Name).HasMaxLength(DepartmentSubcategory.NameMaxLength).IsRequired();
            e.Property(x => x.NormalizedName).HasMaxLength(DepartmentSubcategory.NameMaxLength).IsRequired();
            e.Property(x => x.IsActive).HasDefaultValue(true);
            e.HasOne(x => x.Department).WithMany(d => d.SubcategoryItems).HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Parent).WithMany(p => p.Children).HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
            // No duplicate names at the same level of a department. No filter: SQL Server treats the NULL
            // parents of top-level rows as equal, which is exactly the rule wanted here.
            e.HasIndex(x => new { x.DepartmentId, x.ParentId, x.NormalizedName }).IsUnique().HasFilter(null).HasDatabaseName("UX_DepartmentSubcategories_Name");
            e.HasData(
                Sub(1, 7, null, "Islamic", 1), Sub(2, 7, null, "Christian", 2),
                Sub(3, 7, 1, "Religion", 1), Sub(4, 7, 1, "Quran", 2), Sub(5, 7, 2, "Christian Religion", 1),
                Sub(6, 13, null, "French", 1), Sub(7, 13, null, "Deutsch", 2),
                Sub(8, 10, null, "ICT", 1), Sub(9, 10, null, "AI", 2),
                Sub(10, 15, null, "Wellbeing", 1), Sub(11, 15, null, "Support", 2), Sub(12, 15, null, "Discipline", 3), Sub(13, 15, null, "Character Building", 4));
        });

        b.Entity<ManagementPositionAssignment>(e =>
        {
            e.ToTable("ManagementPositionAssignments", t => t.HasCheckConstraint("CK_PositionAssignments_Position", "[Position] BETWEEN 1 AND 7"));
            e.Property(x => x.EndReason).HasMaxLength(ManagementPositionAssignment.ReasonMaxLength);
            e.Property(x => x.AssignedBy).HasMaxLength(150);
            e.HasOne(x => x.Member).WithMany().HasForeignKey(x => x.ManagementMemberId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.Position).IsUnique().HasFilter("[IsCurrent] = 1").HasDatabaseName("UX_PositionAssignments_CurrentPerPosition");
            e.HasIndex(x => x.ManagementMemberId).IsUnique().HasFilter("[IsCurrent] = 1").HasDatabaseName("UX_PositionAssignments_CurrentPerMember");
            e.HasIndex(x => new { x.Position, x.AssignedAt });
        });

        b.Entity<TimeSlot>(e =>
        {
            e.ToTable("TimeSlots", t =>
            {
                t.HasCheckConstraint("CK_TimeSlots_Number", $"[Number] BETWEEN 1 AND {TimeSlot.Count}");
                t.HasCheckConstraint("CK_TimeSlots_Times", "([StartTime] IS NULL AND [EndTime] IS NULL) OR ([StartTime] IS NOT NULL AND [EndTime] IS NOT NULL AND [EndTime] > [StartTime])");
            });
            e.Property(x => x.Name).HasMaxLength(TimeSlot.NameMaxLength);
            e.Property(x => x.IsActive).HasDefaultValue(true);
            e.HasIndex(x => x.Number).IsUnique();
            // Ten slots without times: the school enters its own times.
            e.HasData(Enumerable.Range(1, TimeSlot.Count).Select(n => new TimeSlot { Id = n, Number = n, IsActive = true, UpdatedAt = SeedTime }));
        });

        ConfigureBelongings(b);

        b.Entity<PeSessionDetail>(e =>
        {
            e.ToTable("PeSessionDetails", t => t.HasCheckConstraint("CK_PeSessionDetails_Facility", "[Facility] IS NULL OR [Facility] BETWEEN 1 AND 3"));
            e.HasKey(x => x.TimetableAssignmentId);
            e.Property(x => x.Activity).HasMaxLength(PeSessionDetail.ActivityMaxLength);
            e.Property(x => x.Notes).HasMaxLength(PeSessionDetail.NotesMaxLength);
            e.Property(x => x.UpdatedBy).HasMaxLength(150);
            e.HasOne(x => x.Session).WithOne().HasForeignKey<PeSessionDetail>(x => x.TimetableAssignmentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.CoTeacher).WithMany().HasForeignKey(x => x.CoTeacherId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.CoTeacherId);
        });

        b.Entity<PeEquipmentItem>(e =>
        {
            e.ToTable("PeEquipmentItems");
            e.Property(x => x.Name).HasMaxLength(PeEquipmentItem.NameMaxLength).IsRequired();
            e.HasOne(x => x.Detail).WithMany(d => d.Equipment).HasForeignKey(x => x.TimetableAssignmentId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.TimetableAssignmentId, x.SortOrder });
        });

        b.Entity<TimetableAssignment>(e =>
        {
            e.ToTable("TimetableAssignments", t =>
            {
                t.HasCheckConstraint("CK_Timetable_SchoolDay", "[Day] BETWEEN 0 AND 4");
                t.HasCheckConstraint("CK_Timetable_EndAfterStart", "[EndedOn] IS NULL OR [EndedOn] >= [StartsOn]");
                t.HasTrigger(TimetableLimitTrigger);
            });
            e.Property(x => x.Subject).HasMaxLength(TimetableAssignment.SubjectMaxLength).IsRequired();
            e.Property(x => x.Notes).HasMaxLength(TimetableAssignment.NotesMaxLength);
            e.Property(x => x.EndReason).HasMaxLength(200);
            e.Property(x => x.CreatedBy).HasMaxLength(150);
            e.Property(x => x.UpdatedBy).HasMaxLength(150);
            e.HasOne(x => x.SchoolClass).WithMany().HasForeignKey(x => x.SchoolClassId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.TimeSlot).WithMany().HasForeignKey(x => x.TimeSlotId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Department).WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Teacher).WithMany().HasForeignKey(x => x.TeacherId).OnDelete(DeleteBehavior.Restrict);
            // One current session per class slot, and a teacher in one class per slot.
            e.HasIndex(x => new { x.SchoolClassId, x.Day, x.TimeSlotId }).IsUnique().HasFilter("[EndedOn] IS NULL").HasDatabaseName("UX_Timetable_ClassSlot");
            e.HasIndex(x => new { x.TeacherId, x.Day, x.TimeSlotId }).IsUnique().HasFilter("[EndedOn] IS NULL").HasDatabaseName("UX_Timetable_TeacherSlot");
            e.HasIndex(x => new { x.TeacherId, x.StartsOn, x.EndedOn });
        });

        b.Entity<DepartmentHeadAssignment>(e =>
        {
            e.ToTable("DepartmentHeadAssignments", t => t.HasCheckConstraint("CK_DepartmentHeads_CurrentHasNoEnd", "[IsCurrent] = 0 OR [EndedAt] IS NULL"));
            e.Property(x => x.EndReason).HasMaxLength(DepartmentHeadAssignment.NoteMaxLength);
            e.Property(x => x.Note).HasMaxLength(DepartmentHeadAssignment.NoteMaxLength);
            e.Property(x => x.AssignedBy).HasMaxLength(150);
            e.HasOne(x => x.Department).WithMany(d => d.HeadAssignments).HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Cascade);
            // History belongs to the person: it goes only if the staff record itself is deleted (a current HOD cannot be deleted).
            e.HasOne(x => x.Teacher).WithMany().HasForeignKey(x => x.TeacherId).OnDelete(DeleteBehavior.Cascade);
            // Exactly one active HOD per department, and one department per active HOD.
            e.HasIndex(x => x.DepartmentId).IsUnique().HasFilter("[IsCurrent] = 1").HasDatabaseName("UX_DepartmentHead_CurrentPerDepartment");
            e.HasIndex(x => x.TeacherId).IsUnique().HasFilter("[IsCurrent] = 1").HasDatabaseName("UX_DepartmentHead_CurrentPerTeacher");
            e.HasIndex(x => new { x.DepartmentId, x.AssignedAt });
        });

        b.Entity<ManagementMember>(e =>
        {
            e.Property(x => x.Email).HasMaxLength(ManagementMember.EmailMaxLength);
            // One holder per Management Team position.
            e.HasIndex(x => x.Position).IsUnique().HasFilter("[Position] IS NOT NULL").HasDatabaseName("UX_StaffMembers_ManagementPosition");
        });

        b.Entity<ManagementTask>(e =>
        {
            e.ToTable("ManagementTasks", t =>
            {
                t.HasCheckConstraint("CK_ManagementTasks_Status", "[Status] BETWEEN 1 AND 4");
                t.HasCheckConstraint("CK_ManagementTasks_Priority", "[Priority] BETWEEN 1 AND 4");
                t.HasCheckConstraint("CK_ManagementTasks_TimeNeedsDate", "[DueTime] IS NULL OR [DueDate] IS NOT NULL");
            });
            e.Property(x => x.Title).HasMaxLength(ManagementTask.TitleMaxLength).IsRequired();
            e.Property(x => x.Description).HasMaxLength(ManagementTask.TextMaxLength);
            e.Property(x => x.Notes).HasMaxLength(ManagementTask.TextMaxLength);
            e.Property(x => x.CreatedBy).HasMaxLength(150);
            e.Property(x => x.UpdatedBy).HasMaxLength(150);
            e.Property(x => x.Status).HasDefaultValue(ManagementTaskStatus.ToDo);
            e.Property(x => x.Priority).HasDefaultValue(TaskPriority.Medium);
            // A member with tasks cannot be deleted: the tasks are reassigned or removed first.
            e.HasOne(x => x.Member).WithMany().HasForeignKey(x => x.ManagementMemberId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.ManagementMemberId, x.Status });
            e.HasIndex(x => new { x.Status, x.DueDate });
        });

        b.Entity<ManagementTaskHistory>(e =>
        {
            e.ToTable("ManagementTaskHistory");
            e.Property(x => x.TaskTitle).HasMaxLength(ManagementTask.TitleMaxLength).IsRequired();
            e.Property(x => x.Summary).HasMaxLength(Entities.ManagementTaskHistory.SummaryMaxLength).IsRequired();
            e.Property(x => x.ChangedBy).HasMaxLength(150);
            e.HasOne(x => x.Member).WithMany().HasForeignKey(x => x.ManagementMemberId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ManagementMemberId, x.ChangedAt });
            e.HasIndex(x => x.TaskId);
        });

        b.Entity<StudentTransferHistory>(e =>
        {
            e.ToTable("StudentTransferHistories");
            e.Property(x => x.TransferredBy).HasMaxLength(150);
            e.HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.FromClass).WithMany().HasForeignKey(x => x.FromClassId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ToClass).WithMany().HasForeignKey(x => x.ToClassId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.StudentId, x.TransferredAt });
        });

        b.Entity<AppSetting>(e =>
        {
            e.ToTable("AppSettings");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(AppSetting.KeyMaxLength);
            e.Property(x => x.Value).HasMaxLength(AppSetting.ValueMaxLength);
        });

        ConfigureOperations(b);
        ConfigureAuditAndArrivals(b);
    }

    private static void ConfigureAuditAndArrivals(ModelBuilder b)
    {
        b.Entity<DutyAssignment>(e =>
        {
            e.Property(x => x.DutyType).HasDefaultValue(DutyType.Morning);
            e.Property(x => x.Status).HasDefaultValue(DutyStatus.Pending);
            e.Property(x => x.AuditNotes).HasMaxLength(DutyAssignment.NotesMaxLength);
            e.Property(x => x.IssuesEncountered).HasMaxLength(DutyAssignment.NotesMaxLength);
            e.Property(x => x.CreatedBy).HasMaxLength(150);
            e.Property(x => x.UpdatedBy).HasMaxLength(150);
            e.HasIndex(x => new { x.DutyDate, x.Status });
            e.HasIndex(x => x.DutyType);
        });

        b.Entity<DutyAudit>(e =>
        {
            e.ToTable("DutyAudits");
            e.Property(x => x.Description).HasMaxLength(DutyAssignment.DescriptionMaxLength).IsRequired();
            e.Property(x => x.Summary).HasMaxLength(DutyAudit.SummaryMaxLength).IsRequired();
            e.Property(x => x.ChangedBy).HasMaxLength(150);
            e.HasIndex(x => new { x.DutyAssignmentId, x.ChangedAt });
            e.HasIndex(x => x.DutyDate);
        });

        b.Entity<DutyStaffAssignment>(e =>
        {
            e.ToTable("DutyStaffAssignments", t => t.HasCheckConstraint("CK_DutyStaffAssignments_Role", "[Role] IN (1, 2)"));
            e.Property(x => x.EndReason).HasMaxLength(DutyStaffAssignment.EndReasonMaxLength);
            e.Property(x => x.AssignedBy).HasMaxLength(150);
            e.HasOne(x => x.DutyAssignment).WithMany(d => d.StaffAssignments).HasForeignKey(x => x.DutyAssignmentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.StaffMember).WithMany().HasForeignKey(x => x.StaffMemberId).OnDelete(DeleteBehavior.Restrict);
            // At most one current primary and one current backup per duty session.
            e.HasIndex(x => new { x.DutyAssignmentId, x.Role }).IsUnique().HasFilter("[IsCurrent] = 1").HasDatabaseName("UX_DutyStaff_CurrentRole");
            e.HasIndex(x => new { x.StaffMemberId, x.AssignedAt });
        });

        b.Entity<StaffDutyFeedback>(e =>
        {
            e.ToTable("StaffDutyFeedback", t => t.HasCheckConstraint("CK_StaffDutyFeedback_Rating", "[Rating] BETWEEN 1 AND 5"));
            e.Property(x => x.Feedback).HasMaxLength(StaffDutyFeedback.FeedbackMaxLength).IsRequired();
            e.Property(x => x.Strengths).HasMaxLength(StaffDutyFeedback.ListMaxLength);
            e.Property(x => x.Issues).HasMaxLength(StaffDutyFeedback.ListMaxLength);
            e.Property(x => x.RecordedBy).HasMaxLength(150);
            // Evaluations are history: an assignment with evaluations cannot be removed.
            e.HasOne(x => x.DutyStaffAssignment).WithMany(a => a.Feedback).HasForeignKey(x => x.DutyStaffAssignmentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Evaluator).WithMany().HasForeignKey(x => x.EvaluatorStaffId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.DutyStaffAssignmentId, x.CreatedAt });
        });

        b.Entity<StudentArrivalRecord>(e =>
        {
            e.ToTable("StudentArrivalRecords", t =>
            {
                t.HasCheckConstraint("CK_StudentArrivals_PresentHasTime", "[Status] <> 1 OR [ArrivalTime] IS NOT NULL");
                t.HasCheckConstraint("CK_StudentArrivals_AbsentHasNoTime", "[Status] <> 2 OR ([ArrivalTime] IS NULL AND [LateStatus] IS NULL)");
            });
            e.Property(x => x.ArrivalLocation).HasMaxLength(StudentArrivalRecord.LocationMaxLength);
            e.Property(x => x.Notes).HasMaxLength(StudentArrivalRecord.NotesMaxLength);
            e.Property(x => x.RecordedBy).HasMaxLength(150);
            e.HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.SchoolClass).WithMany().HasForeignKey(x => x.SchoolClassId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ReceivedBy).WithMany().HasForeignKey(x => x.ReceivedByStaffId).OnDelete(DeleteBehavior.Restrict);
            // One record per student per day; changes go through the correction workflow.
            e.HasIndex(x => new { x.StudentId, x.ArrivalDate }).IsUnique().HasDatabaseName("UX_StudentArrival_StudentDay");
            e.HasIndex(x => new { x.ArrivalDate, x.SchoolClassId });
        });

        b.Entity<StudentArrivalCorrection>(e =>
        {
            e.ToTable("StudentArrivalCorrections");
            e.Property(x => x.Before).HasMaxLength(StudentArrivalCorrection.TextMaxLength).IsRequired();
            e.Property(x => x.After).HasMaxLength(StudentArrivalCorrection.TextMaxLength).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(StudentArrivalCorrection.ReasonMaxLength).IsRequired();
            e.Property(x => x.CorrectedBy).HasMaxLength(150);
            e.HasOne(x => x.Record).WithMany(r => r.Corrections).HasForeignKey(x => x.StudentArrivalRecordId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureOperations(ModelBuilder b)
    {
        b.Entity<DutyAssignment>(e =>
        {
            e.ToTable("DutyAssignments", t =>
            {
                t.HasCheckConstraint("CK_DutyAssignments_Time", "[EndTime] > [StartTime]");
                t.HasCheckConstraint("CK_DutyAssignments_BackupNotPrimary", "[BackupStaffId] IS NULL OR [BackupStaffId] <> [PrimaryStaffId]");
            });
            e.Property(x => x.Description).HasMaxLength(DutyAssignment.DescriptionMaxLength).IsRequired();
            e.Property(x => x.Location).HasMaxLength(DutyAssignment.LocationMaxLength);
            e.Property(x => x.Notes).HasMaxLength(DutyAssignment.NotesMaxLength);
            e.HasOne(x => x.PrimaryStaff).WithMany().HasForeignKey(x => x.PrimaryStaffId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.BackupStaff).WithMany().HasForeignKey(x => x.BackupStaffId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.DutyDate, x.Component });
            e.HasIndex(x => x.PrimaryStaffId);
            e.HasIndex(x => x.BackupStaffId);
        });

        b.Entity<DutyAssignmentClass>(e =>
        {
            e.ToTable("DutyAssignmentClasses");
            e.HasKey(x => new { x.DutyAssignmentId, x.SchoolClassId });
            e.HasOne(x => x.DutyAssignment).WithMany(d => d.Classes).HasForeignKey(x => x.DutyAssignmentId).OnDelete(DeleteBehavior.Cascade);
            // A class used by a duty cannot be deleted (ClassService explains this and offers deactivation).
            e.HasOne(x => x.SchoolClass).WithMany().HasForeignKey(x => x.SchoolClassId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<DutyBackupResponse>(e =>
        {
            e.ToTable("DutyBackupResponses");
            e.Property(x => x.Note).HasMaxLength(DutyBackupResponse.NoteMaxLength);
            e.Property(x => x.RecordedBy).HasMaxLength(150);
            e.HasOne(x => x.DutyAssignment).WithMany(d => d.BackupResponses).HasForeignKey(x => x.DutyAssignmentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.StaffMember).WithMany().HasForeignKey(x => x.StaffMemberId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.DutyAssignmentId, x.RecordedAt });
        });

        b.Entity<TeacherWorkloadAssignment>(e =>
        {
            e.ToTable("TeacherWorkloadAssignments", t =>
            {
                t.HasCheckConstraint("CK_TeacherWorkloadAssignments_Periods", $"[Periods] BETWEEN 1 AND {TeacherWorkloadAssignment.MaxWeeklyPeriods}");
                t.HasCheckConstraint("CK_TeacherWorkloadAssignments_Category", "[Category] BETWEEN 1 AND 4");
                t.HasCheckConstraint("CK_TeacherWorkloadAssignments_ClassNeedsClass", "[Category] <> 1 OR [SchoolClassId] IS NOT NULL");
                t.HasCheckConstraint("CK_TeacherWorkloadAssignments_DutyLink", "[DutyAssignmentId] IS NULL OR [Category] = 3");
                // A period placed in a timetable slot is exactly one period on one school day.
                t.HasCheckConstraint("CK_TeacherWorkloadAssignments_Slot",
                    "([Day] IS NULL AND [TimeSlotId] IS NULL) OR ([Day] BETWEEN 0 AND 4 AND [TimeSlotId] IS NOT NULL AND [Periods] = 1)");
                t.HasTrigger(WorkloadLimitTrigger);
            });
            e.HasOne(x => x.TimeSlot).WithMany().HasForeignKey(x => x.TimeSlotId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.TeacherId, x.WeekStart, x.Day, x.TimeSlotId })
                .IsUnique().HasFilter("[TimeSlotId] IS NOT NULL").HasDatabaseName("UX_Workload_TeacherSlotPerWeek");
            e.Property(x => x.Description).HasMaxLength(TeacherWorkloadAssignment.DescriptionMaxLength);
            e.HasOne(x => x.Teacher).WithMany().HasForeignKey(x => x.TeacherId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.SchoolClass).WithMany().HasForeignKey(x => x.SchoolClassId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.DutyAssignment).WithMany().HasForeignKey(x => x.DutyAssignmentId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.TeacherId, x.WeekStart });
            e.HasIndex(x => new { x.WeekStart, x.Category });
            // One Class row per teacher, week and class (its periods are edited, not duplicated).
            e.HasIndex(x => new { x.TeacherId, x.WeekStart, x.SchoolClassId })
                .IsUnique().HasFilter("[Category] = 1 AND [SchoolClassId] IS NOT NULL")
                .HasDatabaseName("UX_Workload_ClassPerWeek");
            // A recorded duty is counted at most once for a teacher.
            e.HasIndex(x => new { x.TeacherId, x.DutyAssignmentId })
                .IsUnique().HasFilter("[DutyAssignmentId] IS NOT NULL")
                .HasDatabaseName("UX_Workload_DutyOnce");
        });

        b.Entity<WorkloadAssignmentHistory>(e =>
        {
            e.ToTable("WorkloadAssignmentHistory");
            e.Property(x => x.Summary).HasMaxLength(WorkloadAssignmentHistory.SummaryMaxLength).IsRequired();
            e.Property(x => x.ChangedBy).HasMaxLength(150);
            // The log belongs to the teacher: it goes only if the teacher record itself is deleted.
            e.HasOne(x => x.Teacher).WithMany().HasForeignKey(x => x.TeacherId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.TeacherId, x.ChangedAt });
        });
    }

    private static void ConfigureBelongings(ModelBuilder b)
    {
        const string status = "BETWEEN 1 AND 3";
        b.Entity<AcademicYear>(e =>
        {
            e.ToTable("AcademicYears");
            e.Property(x => x.Name).HasMaxLength(AcademicYear.NameMaxLength).IsRequired();
            e.HasIndex(x => x.Name).IsUnique();
        });
        b.Entity<AcademicQuarter>(e =>
        {
            e.ToTable("AcademicQuarters", t =>
            {
                t.HasCheckConstraint("CK_AcademicQuarters_Number", "[Number] BETWEEN 1 AND 4");
                t.HasCheckConstraint("CK_AcademicQuarters_Dates", "([StartDate] IS NULL AND [EndDate] IS NULL) OR ([StartDate] IS NOT NULL AND [EndDate] IS NOT NULL AND [EndDate] >= [StartDate])");
            });
            e.Property(x => x.Name).HasMaxLength(AcademicQuarter.NameMaxLength).IsRequired();
            e.HasOne(x => x.AcademicYear).WithMany(y => y.Quarters).HasForeignKey(x => x.AcademicYearId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.AcademicYearId, x.Number }).IsUnique();
        });
        b.Entity<BelongingsRecord>(e =>
        {
            e.ToTable("BelongingsRecords", t =>
            {
                foreach (var c in new[] { "Jacket", "SchoolBag", "PencilCase", "WaterFlask", "LunchBox" })
                    t.HasCheckConstraint($"CK_BelongingsRecords_{c}", $"[{c}] IS NULL OR [{c}] {status}");
            });
            e.Property(x => x.Notes).HasMaxLength(BelongingsRecord.NotesMaxLength);
            e.Property(x => x.UpdatedBy).HasMaxLength(150);
            // Records are history: a student, class or quarter that has records is not deleted with them.
            e.HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.SchoolClass).WithMany().HasForeignKey(x => x.SchoolClassId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Quarter).WithMany().HasForeignKey(x => x.AcademicQuarterId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.StudentId, x.Date }).IsUnique().HasDatabaseName("UX_Belongings_StudentDay");
            e.HasIndex(x => new { x.AcademicQuarterId, x.Date });
        });
        b.Entity<AcademicMaterialRecord>(e =>
        {
            e.ToTable("AcademicMaterialRecords", t =>
            {
                t.HasCheckConstraint("CK_AcademicMaterials_Book", $"[Book] {status}");
                t.HasCheckConstraint("CK_AcademicMaterials_CopyBook", $"[CopyBook] {status}");
            });
            e.Property(x => x.Subject).HasMaxLength(TimetableAssignment.SubjectMaxLength).IsRequired();
            e.HasOne(x => x.Record).WithMany(r => r.Materials).HasForeignKey(x => x.BelongingsRecordId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Department).WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.BelongingsRecordId, x.DepartmentId, x.Subject }).IsUnique().HasDatabaseName("UX_AcademicMaterials_RecordSubject");
        });
        b.Entity<BelongingsNoteHistory>(e =>
        {
            e.ToTable("BelongingsNoteHistory");
            e.Property(x => x.PreviousNotes).HasMaxLength(BelongingsRecord.NotesMaxLength);
            e.Property(x => x.ChangedBy).HasMaxLength(150);
            e.HasOne(x => x.Record).WithMany().HasForeignKey(x => x.BelongingsRecordId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.BelongingsRecordId, x.ChangedAt });
        });
    }

    private static DepartmentSubcategory Sub(int id, int departmentId, int? parentId, string name, int order) => new()
    {
        Id = id, DepartmentId = departmentId, ParentId = parentId, Name = name, NormalizedName = name.ToUpperInvariant(),
        IsActive = true, SortOrder = order, CreatedAt = SeedTime, UpdatedAt = SeedTime,
    };

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampTimes();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampTimes();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void StampTimes()
    {
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<IAuditable>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Property(x => x.CreatedAt).IsModified = false;
                entry.Entity.UpdatedAt = now;
            }
        }

        foreach (var entry in ChangeTracker.Entries<AppSetting>())
            if (entry.State is EntityState.Added or EntityState.Modified)
                entry.Entity.UpdatedAt = now;
    }
}
