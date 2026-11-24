using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services.OrgStructure;

/// <summary>
/// The official organizational structure of Royal Kings International School (academic year 2026/2027), transcribed from
/// the school's management structure document. Names, job titles and mobile numbers are kept exactly as the source gives
/// them; nothing is added that the source does not state (no floor assignments, no reporting lines, no job title for the
/// unreadable entry). Every row has a stable source key so the import can run any number of times without duplicates.
/// </summary>
public static class OrgStructureSource
{
    public sealed record Tier(string Key, string Name, string Description, OrgSection Section, int Sort);

    /// <summary>A unit; <see cref="Departments"/> = existing academic departments it leads ("Second Language/French" = department/subcategory).</summary>
    public sealed record Unit(string Key, string TierKey, string Name, int Sort, string? Description = null, string[]? Departments = null);

    public sealed record Position(string Key, string UnitKey, string Title, LeadershipRoleType Role, int Sort, string? GradeRange = null);

    public enum PersonKind { Management, Teacher }

    /// <summary>
    /// A person in the source. <see cref="Department"/> (Tier 6 teachers only): the existing department they belong to,
    /// optionally "Department/Subcategory". <see cref="RosterRows"/>: the Teaching Staff 2027 rows that the sources confirm
    /// are this same person (same name and the same role, e.g. a Homeroom Teacher's Home Room row) — the roster row's staff
    /// record is reused (its empty phone filled from this source) instead of creating a second record.
    /// </summary>
    public sealed record Person(string Key, string Name, string Mobile, PersonKind Kind, string? Department = null, int[]? RosterRows = null);

    public sealed record Assignment(string PersonKey, string PositionKey, bool Primary)
    {
        public string Key => $"asg:{PersonKey}:{PositionKey}";
    }

    /// <summary>A person listed in the source without a readable job title: recorded with a review flag, never a position.</summary>
    public sealed record Pending(string PersonKey, string Message);

    public static readonly Tier[] Tiers =
    [
        new("tier-1", "Executive Advisory & Governance", "Tier 1 — the school's executive management and governance.", OrgSection.SchoolLeadership, 1),
        new("tier-2", "Academic Leadership", "Tier 2 — academic leadership of the school.", OrgSection.SchoolLeadership, 2),
        new("tier-3", "Parents Care, PR & Counseling", "Tier 3 — parents care, public relations, admissions, reception, counseling and discipline.", OrgSection.SchoolLeadership, 3),
        new("tier-4", "Floor Supervisors", "Tier 4 — floor supervision. The source does not say which supervisor covers which floor.", OrgSection.OperationTeam, 4),
        new("tier-5", "Administrative & Operational Management", "Tier 5 — administrative and operational units.", OrgSection.OperationTeam, 5),
        new("tier-6", "Academic Department Leadership", "Tier 6 — Heads of Department, coordinators and team leaders (not the full teaching staff).", OrgSection.SchoolLeadership, 6),
        new("tier-7", "Homeroom & Co-Teachers", "The Homeroom Teachers and Co-Teachers listed in the official staff document. Class allocations come only from Teaching Staff 2027.", OrgSection.AcademicStaff, 7),
    ];

    public static readonly Unit[] Units =
    [
        new("unit-executive", "tier-1", "Executive Directorate", 1),
        new("unit-academic-leadership", "tier-2", "Academic Leadership", 1),
        new("unit-parents-care", "tier-3", "Parents Care", 1),
        new("unit-pr", "tier-3", "Public Relations (PR)", 2),
        new("unit-admissions", "tier-3", "Admissions", 3),
        new("unit-reception", "tier-3", "Reception", 4),
        new("unit-call-center", "tier-3", "Call Center", 5),
        new("unit-counseling", "tier-3", "Counseling", 6),
        new("unit-discipline", "tier-3", "Discipline Office", 7),
        new("unit-floor-supervisors", "tier-4", "Floor Supervisors", 1, "Floor assignments are configured by an authorized administrator; the source does not specify them."),
        new("unit-ops-conduct", "tier-5", "Operations & Student Conduct Department", 1),
        new("unit-facilities-infra", "tier-5", "Facilities & Infrastructure Unit", 2),
        new("unit-safety", "tier-5", "Campus Safety & Security Unit", 3),
        new("unit-it-logistics", "tier-5", "IT & Logistics Units", 4),
        new("unit-lms-support", "tier-5", "LMS Support", 5),
        new("unit-copy-center", "tier-5", "Copy Center", 6),
        new("unit-bookstore", "tier-5", "Bookstore & Student Distribution", 7),
        new("unit-storage", "tier-5", "Storage & Inventory", 8),
        new("unit-inclusion", "tier-5", "Inclusion Department", 9),
        new("unit-facilities-maintenance", "tier-5", "Facilities & Maintenance", 10),
        new("unit-dept-english", "tier-6", "English Department (English - ESS - CB)", 1, "Leadership of English, ESS and CB.", ["English", "ESS"]),
        new("unit-dept-math", "tier-6", "Math Department", 2, null, ["Math"]),
        new("unit-dept-arabic-religion", "tier-6", "Arabic & Religion Department", 3, "A combined leadership grouping; Arabic and Religion remain separate departments.", ["Arabic", "Religion"]),
        new("unit-dept-ass", "tier-6", "Arabic Social Studies", 4, null, ["ASS"]),
        new("unit-dept-french", "tier-6", "French Department", 5, null, ["Second Language/French"]),
        new("unit-dept-german", "tier-6", "German Department", 6, null, ["Second Language/Deutsch"]),
        new("unit-homeroom", "tier-7", "Homeroom Teachers", 1, "The source lists the Homeroom Teachers; their classes come from the Teaching Staff 2027 Home Room rows."),
        new("unit-co-teachers", "tier-7", "Co-Teachers", 2, "The source lists the Co-Teachers without classes; none is invented."),
    ];

    public static readonly Position[] Positions =
    [
        // Tier 1
        new("pos-principal", "unit-executive", "School Principal / Head of School", LeadershipRoleType.Executive, 1),
        new("pos-consultant", "unit-executive", "School Consultant", LeadershipRoleType.Executive, 2),
        new("pos-deputy-headmaster", "unit-executive", "School Deputy Headmaster", LeadershipRoleType.Executive, 3),
        new("pos-academic-dean", "unit-executive", "Academic Dean", LeadershipRoleType.Executive, 4),
        new("pos-qa-manager", "unit-executive", "Quality Assurance Manager", LeadershipRoleType.Executive, 5),
        new("pos-assistant-principal", "unit-executive", "Assistant Principal", LeadershipRoleType.Executive, 6),
        // Tier 2
        new("pos-academic-deputy", "unit-academic-leadership", "Academic Deputy", LeadershipRoleType.AcademicLeadership, 1),
        new("pos-academic-head", "unit-academic-leadership", "Academic Head", LeadershipRoleType.AcademicLeadership, 2, "Grades 2–10"),
        new("pos-head-of-stages", "unit-academic-leadership", "Head of Stages", LeadershipRoleType.AcademicLeadership, 3, "Grades 2–6"),
        new("pos-lms-reviser", "unit-academic-leadership", "Academic LMS Reviser", LeadershipRoleType.Specialist, 4),
        new("pos-hr", "unit-academic-leadership", "Human Resources (HR)", LeadershipRoleType.OperationsAdmin, 5),
        // Tier 3
        new("pos-parents-care", "unit-parents-care", "Parents Care", LeadershipRoleType.Support, 1),
        new("pos-pr", "unit-pr", "Public Relations (PR)", LeadershipRoleType.Specialist, 1),
        new("pos-admissions-tour", "unit-admissions", "Admissions Tour Specialist", LeadershipRoleType.Specialist, 1),
        new("pos-receptionist", "unit-reception", "School Receptionist", LeadershipRoleType.Support, 1),
        new("pos-call-center", "unit-call-center", "School Call Center Agent (complaint)", LeadershipRoleType.Support, 1),
        new("pos-psychological", "unit-counseling", "School Psychological", LeadershipRoleType.Specialist, 1),
        new("pos-discipline", "unit-discipline", "Discipline Officer Department", LeadershipRoleType.Specialist, 1),
        // Tier 4
        new("pos-floor-supervisor", "unit-floor-supervisors", "Floor Supervisor", LeadershipRoleType.FloorSupervisor, 1),
        // Tier 5 — where the source names only the unit, the position carries the unit's own label (no title is invented).
        new("pos-ops-conduct", "unit-ops-conduct", "Operations & Student Conduct Department", LeadershipRoleType.OperationsAdmin, 1),
        new("pos-facilities-infra", "unit-facilities-infra", "Facilities & Infrastructure Unit", LeadershipRoleType.OperationsAdmin, 1),
        new("pos-safety", "unit-safety", "Campus Safety & Security Unit", LeadershipRoleType.OperationsAdmin, 1),
        new("pos-it-logistics", "unit-it-logistics", "IT & Logistics Units", LeadershipRoleType.Operational, 1),
        new("pos-lms-support", "unit-lms-support", "LMS Support", LeadershipRoleType.Support, 1),
        new("pos-copy-center", "unit-copy-center", "Copy Center Technician / Operator", LeadershipRoleType.Operational, 1),
        new("pos-bookstore", "unit-bookstore", "Bookstore & Student Distribution Coordinator", LeadershipRoleType.Coordinator, 1),
        new("pos-storage", "unit-storage", "Storage Room / Inventory Clerk", LeadershipRoleType.Operational, 1),
        new("pos-inclusion", "unit-inclusion", "Inclusion Department", LeadershipRoleType.Specialist, 1),
        new("pos-facilities-maintenance", "unit-facilities-maintenance", "Facilities & Maintenance Supervisor", LeadershipRoleType.OperationsAdmin, 1),
        // Tier 6
        new("pos-english-tl-1-3", "unit-dept-english", "Team Leader", LeadershipRoleType.TeamLeader, 1, "Grades 1–3"),
        new("pos-english-tl-4-10", "unit-dept-english", "Team Leader", LeadershipRoleType.TeamLeader, 2, "Grades 4–10"),
        new("pos-math-hod", "unit-dept-math", "HOD", LeadershipRoleType.Hod, 1, "PreK to Grade 9"),
        new("pos-math-coordinator", "unit-dept-math", "Math Coordinator", LeadershipRoleType.Coordinator, 2, "Grades 2–9"),
        new("pos-math-tl", "unit-dept-math", "Team Leader", LeadershipRoleType.TeamLeader, 3, "PreK to Grade 1"),
        new("pos-arabic-religion-tl", "unit-dept-arabic-religion", "Team Leader", LeadershipRoleType.TeamLeader, 1, "PreK to G3"),
        new("pos-ass-hod", "unit-dept-ass", "HOD", LeadershipRoleType.Hod, 1),
        new("pos-french-hod", "unit-dept-french", "HOD", LeadershipRoleType.Hod, 1),
        new("pos-german-hod-4-10", "unit-dept-german", "HOD", LeadershipRoleType.Hod, 1, "Grades 4–10"),
        new("pos-german-hod-prek-3", "unit-dept-german", "HOD", LeadershipRoleType.Hod, 2, "PreK to Grade 3"),
        // Tier 7
        new("pos-homeroom", "unit-homeroom", "Homeroom Teacher", LeadershipRoleType.HomeroomTeacher, 1),
        new("pos-co-teacher", "unit-co-teachers", "Co-Teacher", LeadershipRoleType.CoTeacher, 1),
    ];

    public static readonly Person[] People =
    [
        // Tier 1
        new("p-noha-osama", "Dr. Noha Osama", "01111219254", PersonKind.Management),
        new("p-reham-gamil", "Dr. Reham Gamil", "01009602017", PersonKind.Management),
        new("p-mohamed-abdul-rasoul", "Mr. Mohamed Abdul-Rasoul", "01004748355", PersonKind.Management),
        new("p-rania-hijazi", "Dr. Rania Moh. Hijazi", "01020975325", PersonKind.Management),
        new("p-nour-aladawy", "Ms. Nour Aladawy", "01100626917", PersonKind.Management),
        new("p-farah-akram", "Farah Akram", "01061182914", PersonKind.Management),
        // Tier 2
        new("p-wessam-alaa", "Ms. Wessam Alaa", "01020316559", PersonKind.Management),
        new("p-basma-farghali", "Ms. Basma Farghali", "01067103468", PersonKind.Management),
        new("p-eman-gooda", "Ms. Eman Gooda", "01028355886", PersonKind.Teacher, "English"),
        new("p-maha-ismael", "Ms. Maha Ismael", "01067823339", PersonKind.Management),
        // Tier 3
        new("p-martha", "Ms. Martha", "01277876226", PersonKind.Management),
        new("p-marwa-safwat", "Ms. Marwa Safwat", "01001224414", PersonKind.Management),
        new("p-merna-ahmed", "Ms. Merna Ahmed", "01274961909", PersonKind.Management),
        new("p-manar-eha", "Ms. Manar Eha", "01127198157", PersonKind.Management),
        new("p-mai-ahmed", "Ms. Mai Ahmed", "01014654606", PersonKind.Management),
        new("p-saja-ahmed", "Ms. Saja Ahmed", "01007298812", PersonKind.Management),
        new("p-marian-soliman", "Ms. Marian Soliman", "01023585017", PersonKind.Management),
        // Tier 4
        new("p-donia-mostafa", "Ms. Donia Mostafa", "01145309275", PersonKind.Management),
        new("p-mahmoud-brakat", "Mr. Mahmoud Brakat", "01025172837", PersonKind.Management),
        new("p-ashraf-sultan", "Mr. Ashraf Sultan", "01102277491", PersonKind.Management),
        new("p-bassant-mamdouh", "Ms. Bassant Mamdouh", "01000300977", PersonKind.Management),
        // Tier 5
        new("p-mohamed-abdelrasol", "Mr. Mohamed Abdelrasol", "01025172837", PersonKind.Management),
        new("p-yusuf-shabaan", "Mr. Yusuf Shabaan", "01225569068", PersonKind.Management),
        new("p-waleed", "Mr. Waleed", "01112188998", PersonKind.Management),
        new("p-manal-abdelraouf", "Ms. Manal Abdelraouf", "01008484747", PersonKind.Management),
        new("p-samar-mohsen", "Ms. Samar Mohsen", "01283325410", PersonKind.Management),
        new("p-sara-mostafa", "Ms. Sara Mostafa", "01096247514", PersonKind.Management),
        new("p-nada-hashish", "Dr. Nada Hashish", "01008383056", PersonKind.Management),
        new("p-mahmoud-khalil", "Mr. Mahmoud Khalil", "01098782036", PersonKind.Management),
        // Tier 6 — department leaders, members of their existing academic department
        new("p-saritha", "Ms. Saritha", "01091170531", PersonKind.Teacher, "English"),
        new("p-mamdouh-awaad", "Mr. Mamdouh Awaad", "01201725151", PersonKind.Teacher, "Math"),
        new("p-mohamed-ali-math", "Mr. Mohamed Ali", "01112191030", PersonKind.Teacher, "Math"),
        new("p-salma-awaad", "Ms. Salma Awaad", "01125038577", PersonKind.Teacher, "Math"),
        new("p-eman-ahmed", "Ms. Eman Ahmed", "01101272543", PersonKind.Teacher, "Arabic"),
        new("p-fatma-salah", "Ms. Fatma Salah", "01009149132", PersonKind.Teacher, "ASS"),
        new("p-enjy-salah", "Ms. Enjy Salah", "01005645393", PersonKind.Teacher, "Second Language/French"),
        new("p-habiba-essam", "Ms. Habiba Essam", "01146642303", PersonKind.Teacher, "Second Language/Deutsch"),
        new("p-mohamed-ali-german", "Mr. Mohamed Ali", "01032853184", PersonKind.Teacher, "Second Language/Deutsch"),
        // Listed without a readable job title
        new("p-nour-tarek", "Mr. Nour Tarek", "01020492629", PersonKind.Management),
        // Homeroom Teachers — the same people as Teaching Staff 2027 rows 60–64 (same name, Home Room role)
        new("p-hr-nada", "Nada", "01002032387", PersonKind.Teacher, RosterRows: [61]),
        new("p-hr-jane", "Jane", "01272436333", PersonKind.Teacher, RosterRows: [62]),
        new("p-hr-adrea", "Adrea", "01153608554", PersonKind.Teacher, RosterRows: [64]),
        new("p-hr-nessly", "Nessly", "01016136554", PersonKind.Teacher, RosterRows: [60]),
        new("p-hr-milka", "Milka", "01065942767", PersonKind.Teacher, RosterRows: [63]),
        // Co-Teachers (no classes in the source). Heba Gamal is also Teaching Staff 2027 row 56 (exact full name).
        new("p-co-rawan-mahmoud", "Rawan Mahmoud", "01094400902", PersonKind.Teacher),
        new("p-co-sara-khaled", "Sara Khaled", "01156017785", PersonKind.Teacher),
        new("p-co-nourhan-ahmed", "Nourhan Ahmed", "01021928595", PersonKind.Teacher),
        new("p-co-elham-kassem", "Elham Kassem", "01002005475", PersonKind.Teacher),
        new("p-co-heba-gamal", "Heba Gamal", "01007991592", PersonKind.Teacher, RosterRows: [56]),
        new("p-co-habiba-hussain", "Habiba Hussain", "01154314646", PersonKind.Teacher),
        new("p-co-basmala-atef", "Basmala Atef", "01103862265", PersonKind.Teacher),
        new("p-co-heba-ibrahim", "Heba Ibrahim", "01157595821", PersonKind.Teacher),
        new("p-co-huda-mohamed", "Huda Mohamed", "01117404597", PersonKind.Teacher),
    ];

    public static readonly Assignment[] Assignments =
    [
        // Tier 1
        new("p-noha-osama", "pos-principal", true),
        new("p-reham-gamil", "pos-consultant", true),
        new("p-mohamed-abdul-rasoul", "pos-deputy-headmaster", true),
        new("p-rania-hijazi", "pos-academic-dean", true),
        new("p-nour-aladawy", "pos-qa-manager", true),
        new("p-farah-akram", "pos-assistant-principal", true),
        // Tier 2
        new("p-wessam-alaa", "pos-academic-deputy", true),
        new("p-basma-farghali", "pos-academic-head", true),
        new("p-basma-farghali", "pos-head-of-stages", false),
        new("p-eman-gooda", "pos-lms-reviser", true),
        new("p-maha-ismael", "pos-hr", true),
        // Tier 3
        new("p-martha", "pos-parents-care", true),
        new("p-marwa-safwat", "pos-pr", true),
        new("p-merna-ahmed", "pos-admissions-tour", true),
        new("p-manar-eha", "pos-receptionist", true),
        new("p-merna-ahmed", "pos-receptionist", false),
        new("p-mai-ahmed", "pos-call-center", true),
        new("p-saja-ahmed", "pos-psychological", true),
        new("p-marian-soliman", "pos-discipline", true),
        // Tier 4
        new("p-donia-mostafa", "pos-floor-supervisor", true),
        new("p-mahmoud-brakat", "pos-floor-supervisor", true),
        new("p-ashraf-sultan", "pos-floor-supervisor", true),
        new("p-bassant-mamdouh", "pos-floor-supervisor", true),
        // Tier 5
        new("p-mohamed-abdelrasol", "pos-ops-conduct", true),
        new("p-yusuf-shabaan", "pos-ops-conduct", true),
        new("p-mohamed-abdelrasol", "pos-facilities-infra", false),
        new("p-yusuf-shabaan", "pos-facilities-infra", false),
        new("p-mohamed-abdelrasol", "pos-safety", false),
        new("p-yusuf-shabaan", "pos-safety", false),
        new("p-waleed", "pos-it-logistics", true),
        new("p-marwa-safwat", "pos-lms-support", false),
        new("p-manal-abdelraouf", "pos-lms-support", true),
        new("p-samar-mohsen", "pos-copy-center", true),
        new("p-donia-mostafa", "pos-bookstore", false),
        new("p-sara-mostafa", "pos-bookstore", true),
        new("p-donia-mostafa", "pos-storage", false),
        new("p-nada-hashish", "pos-inclusion", true),
        new("p-mahmoud-khalil", "pos-inclusion", true),
        new("p-donia-mostafa", "pos-facilities-maintenance", false),
        // Tier 6
        new("p-saritha", "pos-english-tl-1-3", true),
        new("p-eman-gooda", "pos-english-tl-4-10", false),
        new("p-mamdouh-awaad", "pos-math-hod", true),
        new("p-mohamed-ali-math", "pos-math-coordinator", true),
        new("p-salma-awaad", "pos-math-tl", true),
        new("p-eman-ahmed", "pos-arabic-religion-tl", true),
        new("p-fatma-salah", "pos-ass-hod", true),
        new("p-enjy-salah", "pos-french-hod", true),
        new("p-habiba-essam", "pos-german-hod-4-10", true),
        new("p-mohamed-ali-german", "pos-german-hod-prek-3", true),
        // Tier 7 — a Homeroom / Co-Teacher position is the person's primary one only when they hold no other
        new("p-hr-nada", "pos-homeroom", true),
        new("p-hr-jane", "pos-homeroom", true),
        new("p-hr-adrea", "pos-homeroom", true),
        new("p-hr-nessly", "pos-homeroom", true),
        new("p-hr-milka", "pos-homeroom", true),
        new("p-co-rawan-mahmoud", "pos-co-teacher", true),
        new("p-co-sara-khaled", "pos-co-teacher", true),
        new("p-co-nourhan-ahmed", "pos-co-teacher", true),
        new("p-co-elham-kassem", "pos-co-teacher", true),
        new("p-co-heba-gamal", "pos-co-teacher", true),
        new("p-co-habiba-hussain", "pos-co-teacher", true),
        new("p-co-basmala-atef", "pos-co-teacher", true),
        new("p-co-heba-ibrahim", "pos-co-teacher", true),
        new("p-co-huda-mohamed", "pos-co-teacher", true),
    ];

    public static readonly Pending[] PendingConfirmation =
    [
        new("p-nour-tarek", "The official structure lists Mr. Nour Tarek (01020492629), but his job title is unreadable in the source. " +
                            "No position is assigned until it is confirmed."),
    ];
}
