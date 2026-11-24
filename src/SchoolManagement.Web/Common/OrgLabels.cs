using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Common;

/// <summary>Display text and links of the organizational structure (labels only; the data lives in the database).</summary>
public static class OrgLabels
{
    public const string OrgChartHref = "/management/org-chart";
    public const string ExecutiveHref = "/management/executive";
    public const string AcademicLeadershipHref = "/management/academic-leadership";
    public const string OperationsHref = "/management/operations";
    public const string DepartmentsLeadershipHref = "/management/departments-leadership";
    public const string PositionsHref = "/management/positions-management";

    /// <summary>The text shown when a person's job title could not be read from the source.</summary>
    public const string PendingPosition = "Position requires confirmation";

    public static readonly LeadershipRoleType[] RoleTypes = Enum.GetValues<LeadershipRoleType>();
    public static readonly OrgSection[] Sections = Enum.GetValues<OrgSection>();

    public static string Section(OrgSection s) => s switch
    {
        OrgSection.SchoolLeadership => ManagementTeams.LeadershipName,
        OrgSection.OperationTeam => ManagementTeams.OperationName,
        OrgSection.AcademicStaff => "Academic Staff",
        _ => s.ToString(),
    };

    public static string SectionHref(OrgSection s) => s switch
    {
        OrgSection.OperationTeam => ManagementTeams.OperationHref,
        OrgSection.AcademicStaff => "/staff/all?category=academic",
        _ => ManagementTeams.LeadershipHref,
    };

    public static string RoleType(LeadershipRoleType r) => r switch
    {
        LeadershipRoleType.Executive => "Executive",
        LeadershipRoleType.AcademicLeadership => "Academic leadership",
        LeadershipRoleType.Hod => "Head of Department",
        LeadershipRoleType.Coordinator => "Coordinator",
        LeadershipRoleType.TeamLeader => "Team Leader",
        LeadershipRoleType.FloorSupervisor => "Floor Supervisor",
        LeadershipRoleType.OperationsAdmin => "Operations & administration",
        LeadershipRoleType.Specialist => "Specialist",
        LeadershipRoleType.Support => "Support",
        LeadershipRoleType.Operational => "Operational",
        LeadershipRoleType.HomeroomTeacher => "Homeroom Teacher",
        LeadershipRoleType.CoTeacher => "Co-Teacher",
        _ => r.ToString(),
    };

    /// <summary>Badge tone of a role type (existing badge / tone classes).</summary>
    public static string RoleTone(LeadershipRoleType r) => r switch
    {
        LeadershipRoleType.Executive => "violet",
        LeadershipRoleType.AcademicLeadership => "blue",
        LeadershipRoleType.Hod => "amber",
        LeadershipRoleType.Coordinator => "teal",
        LeadershipRoleType.TeamLeader => "green",
        LeadershipRoleType.FloorSupervisor => "indigo",
        LeadershipRoleType.HomeroomTeacher or LeadershipRoleType.CoTeacher => "teal",
        _ => "rose",
    };

    public static string RoleIcon(LeadershipRoleType r) => r switch
    {
        LeadershipRoleType.Executive => "crown",
        LeadershipRoleType.AcademicLeadership => "graduation-cap",
        LeadershipRoleType.Hod => "shield-check",
        LeadershipRoleType.Coordinator => "layers",
        LeadershipRoleType.TeamLeader => "users",
        LeadershipRoleType.FloorSupervisor => "school",
        LeadershipRoleType.HomeroomTeacher or LeadershipRoleType.CoTeacher => "graduation-cap",
        _ => "briefcase",
    };

    /// <summary>
    /// A position's title with its grade range, as the source writes it: "Academic Head (Grades 2–10)". The range is not
    /// repeated when the title already contains it.
    /// </summary>
    public static string PositionTitle(string title, string? gradeRange) =>
        string.IsNullOrWhiteSpace(gradeRange) || title.Contains(gradeRange, StringComparison.OrdinalIgnoreCase) ? title : $"{title} ({gradeRange})";

    public static string FlagKind(DataQualityFlagKind k) => k switch
    {
        DataQualityFlagKind.PositionRequiresConfirmation => PendingPosition,
        DataQualityFlagKind.SharedPhoneNumber => "Shared phone number",
        DataQualityFlagKind.IdentityReview => "Identity needs review",
        _ => k.ToString(),
    };

    /// <summary>The person's existing profile page (Management Team profile or staff profile).</summary>
    public static string ProfileHref(int staffId, bool isManagement) => isManagement ? $"/management/members/{staffId}" : $"/staff/{staffId}";
}
