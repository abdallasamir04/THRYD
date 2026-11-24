using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services;

public partial class OrgStructureService
{
    public async Task<ServiceResult> SeedPhase1RoyalKingsStructureAsync(CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // 1. Ensure 6 Tiers
            var tiers = new[]
            {
                (Level: 1, Name: "Executive Advisory & Governance Tier", Desc: "Highest management, advisory, and governance tier of the school.", Order: 1),
                (Level: 2, Name: "Academic Leadership Tier", Desc: "Academic deputies, stage heads, LMS revisers, and HR leadership.", Order: 2),
                (Level: 3, Name: "Parents Care, PR & Counseling Tier", Desc: "Public relations, admissions, reception, psychological support, and discipline.", Order: 3),
                (Level: 4, Name: "Floor Supervisors", Desc: "Floor supervision and hallway student oversight.", Order: 4),
                (Level: 5, Name: "Administrative & Operational Tier", Desc: "Campus safety, facilities, logistics, IT, copy center, bookstore, and operations.", Order: 5),
                (Level: 6, Name: "Academic Department Leadership", Desc: "Heads of Departments (HODs), Coordinators, and Team Leaders.", Order: 6),
            };

            var tierEntities = new Dictionary<int, OrgTier>();
            foreach (var t in tiers)
            {
                var norm = TextNormalizer.NameKey(t.Name);
                var tier = await db.OrgTiers.FirstOrDefaultAsync(x => x.TierLevel == t.Level, ct);
                if (tier == null)
                {
                    tier = new OrgTier
                    {
                        TierLevel = t.Level,
                        Name = t.Name,
                        NormalizedName = norm,
                        Description = t.Desc,
                        SortOrder = t.Order,
                        IsActive = true,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                    db.OrgTiers.Add(tier);
                    await db.SaveChangesAsync(ct);
                }
                tierEntities[t.Level] = tier;
            }

            // 2. Ensure Organizational Units
            var unitsToSeed = new[]
            {
                // Level 1
                (Tier: 1, Name: "Executive Advisory & Governance", Code: "EXEC", Type: OrgUnitType.Governance, Desc: "Executive school management", DeptId: (int?)null),
                // Level 2
                (Tier: 2, Name: "Academic Leadership", Code: "ACAD-LEAD", Type: OrgUnitType.AcademicLeadership, Desc: "Academic deputies and stage heads", DeptId: (int?)null),
                // Level 3
                (Tier: 3, Name: "Parents Care, PR & Counseling", Code: "PR-COUNSEL", Type: OrgUnitType.SupportService, Desc: "Parents care, public relations and student counseling", DeptId: (int?)null),
                // Level 4
                (Tier: 4, Name: "Floor Supervisors", Code: "FLOOR-SUP", Type: OrgUnitType.Supervisory, Desc: "Floor supervision team", DeptId: (int?)null),
                // Level 5
                (Tier: 5, Name: "Operations & Student Conduct Department", Code: "OPS-COND", Type: OrgUnitType.OperationalUnit, Desc: "Operations and conduct management", DeptId: (int?)null),
                (Tier: 5, Name: "Facilities & Infrastructure Unit", Code: "FAC-INFRA", Type: OrgUnitType.OperationalUnit, Desc: "Campus facilities and infrastructure", DeptId: (int?)null),
                (Tier: 5, Name: "Campus Safety & Security Unit", Code: "SAFETY-SEC", Type: OrgUnitType.OperationalUnit, Desc: "Campus safety, security and emergency response", DeptId: (int?)null),
                (Tier: 5, Name: "IT & Logistics Units", Code: "IT-LOG", Type: OrgUnitType.OperationalUnit, Desc: "Information technology and logistics", DeptId: (int?)null),
                (Tier: 5, Name: "LMS Support", Code: "LMS-SUP", Type: OrgUnitType.SupportService, Desc: "Learning management system support", DeptId: (int?)null),
                (Tier: 5, Name: "Copy Center", Code: "COPY-CTR", Type: OrgUnitType.OperationalUnit, Desc: "Printing and copy center operations", DeptId: (int?)null),
                (Tier: 5, Name: "Bookstore & Student Distribution", Code: "BOOKSTORE", Type: OrgUnitType.OperationalUnit, Desc: "Bookstore and student material distribution", DeptId: (int?)null),
                (Tier: 5, Name: "Storage Room / Inventory", Code: "INVENTORY", Type: OrgUnitType.OperationalUnit, Desc: "Storage room inventory control", DeptId: (int?)null),
                (Tier: 5, Name: "Inclusion Department", Code: "INCLUSION", Type: OrgUnitType.SupportService, Desc: "Special educational needs and student inclusion", DeptId: (int?)null),
                (Tier: 5, Name: "Facilities & Maintenance", Code: "FAC-MAINT", Type: OrgUnitType.OperationalUnit, Desc: "Facilities maintenance and repair", DeptId: (int?)null),
                // Level 6: Academic Departments
                (Tier: 6, Name: "English Department (English – ESS – CB)", Code: "ENG-DEPT", Type: OrgUnitType.AcademicDepartment, Desc: "English, ESS and Character Building", DeptId: (int?)2),
                (Tier: 6, Name: "Math Department", Code: "MATH-DEPT", Type: OrgUnitType.AcademicDepartment, Desc: "Mathematics Department PreK to Grade 10", DeptId: (int?)3),
                (Tier: 6, Name: "Arabic & Religion Department", Code: "ARB-REL-DEPT", Type: OrgUnitType.AcademicDepartment, Desc: "Arabic Language and Religious Studies", DeptId: (int?)1),
                (Tier: 6, Name: "Arabic Social Studies", Code: "ASS-DEPT", Type: OrgUnitType.AcademicDepartment, Desc: "Arabic Social Studies (ASS)", DeptId: (int?)5),
                (Tier: 6, Name: "French Department", Code: "FR-DEPT", Type: OrgUnitType.AcademicDepartment, Desc: "French Second Language", DeptId: (int?)13),
                (Tier: 6, Name: "German Department", Code: "GER-DEPT", Type: OrgUnitType.AcademicDepartment, Desc: "German Second Language", DeptId: (int?)13),
                (Tier: 6, Name: "Science Department", Code: "SCI-DEPT", Type: OrgUnitType.AcademicDepartment, Desc: "Science Department", DeptId: (int?)4),
                (Tier: 6, Name: "Activities Department", Code: "ACT-DEPT", Type: OrgUnitType.AcademicDepartment, Desc: "School Activities, Sports and Arts", DeptId: (int?)null),
            };

            var unitEntities = new Dictionary<string, OrgUnit>();
            int uOrder = 1;
            foreach (var u in unitsToSeed)
            {
                var norm = TextNormalizer.NameKey(u.Name);
                var tier = tierEntities[u.Tier];
                var unit = await db.OrgUnits.FirstOrDefaultAsync(x => x.NormalizedName == norm && x.TierId == tier.Id, ct);
                if (unit == null)
                {
                    unit = new OrgUnit
                    {
                        Name = u.Name,
                        NormalizedName = norm,
                        Code = u.Code,
                        TierId = tier.Id,
                        UnitType = u.Type,
                        Description = u.Desc,
                        DepartmentId = u.DeptId,
                        SortOrder = uOrder++,
                        IsActive = true,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                    db.OrgUnits.Add(unit);
                    await db.SaveChangesAsync(ct);
                }
                unitEntities[u.Name] = unit;
            }

            // 3. Helper to Find or Create Staff Member
            async Task<StaffMember> FindOrCreateStaffAsync(string name, string phone, string jobTitle, bool isManagement = false, int? deptId = null, string? notes = null)
            {
                var cleanPhone = TextNormalizer.CleanPhone(phone);
                var phoneKey = TextNormalizer.PhoneKey(cleanPhone);
                var nameKey = TextNormalizer.NameKey(name);

                // Find by phone first, but only if the name is compatible (do not merge different people sharing a number like Mahmoud Brakat & Mohamed Abdelrasol)
                StaffMember? existing = null;
                if (!string.IsNullOrEmpty(phoneKey))
                {
                    var phoneMatches = await db.StaffMembers.Where(s => s.NormalizedContact == phoneKey).ToListAsync(ct);
                    existing = phoneMatches.FirstOrDefault(s =>
                        s.NormalizedName == nameKey ||
                        s.NormalizedName.Contains(nameKey) ||
                        nameKey.Contains(s.NormalizedName) ||
                        (s.NormalizedName.Contains("mohamed") && nameKey.Contains("mohamed") && !s.NormalizedName.Contains("brakat") && !nameKey.Contains("brakat")) ||
                        (s.NormalizedName.Contains("brakat") && nameKey.Contains("brakat")));
                }

                if (existing != null)
                {
                    if (string.IsNullOrWhiteSpace(existing.JobTitle) || existing.JobTitle == "Management Team")
                    {
                        existing.JobTitle = jobTitle;
                    }
                    if (existing is ManagementMember mmExisting && mmExisting.Position == null && mmExisting.CustomPositionId == null && !string.IsNullOrWhiteSpace(jobTitle))
                    {
                        var custPosNorm = TextNormalizer.NameKey(jobTitle);
                        var custPos = await db.ManagementCustomPositions.FirstOrDefaultAsync(x => x.NormalizedTitle == custPosNorm, ct);
                        if (custPos == null)
                        {
                            custPos = new ManagementCustomPosition
                            {
                                Title = jobTitle,
                                NormalizedTitle = custPosNorm,
                                CreatedAt = DateTime.UtcNow,
                                CreatedBy = "Phase1Seeder"
                            };
                            db.ManagementCustomPositions.Add(custPos);
                            await db.SaveChangesAsync(ct);
                        }
                        mmExisting.CustomPositionId = custPos.Id;
                    }
                    await db.SaveChangesAsync(ct);
                    return existing;
                }

                // If not found by phone, check by normalized name
                var existingByName = await db.StaffMembers.FirstOrDefaultAsync(s => s.NormalizedName == nameKey, ct);
                if (existingByName != null)
                {
                    if (string.IsNullOrEmpty(existingByName.NormalizedContact) || existingByName.NormalizedContact == phoneKey)
                    {
                        existingByName.ContactNumber = cleanPhone;
                        existingByName.NormalizedContact = phoneKey;
                    }
                    if (string.IsNullOrWhiteSpace(existingByName.JobTitle) || existingByName.JobTitle == "Management Team")
                    {
                        existingByName.JobTitle = jobTitle;
                    }
                    if (existingByName is ManagementMember mmByName && mmByName.Position == null && mmByName.CustomPositionId == null && !string.IsNullOrWhiteSpace(jobTitle))
                    {
                        var custPosNorm = TextNormalizer.NameKey(jobTitle);
                        var custPos = await db.ManagementCustomPositions.FirstOrDefaultAsync(x => x.NormalizedTitle == custPosNorm, ct);
                        if (custPos == null)
                        {
                            custPos = new ManagementCustomPosition
                            {
                                Title = jobTitle,
                                NormalizedTitle = custPosNorm,
                                CreatedAt = DateTime.UtcNow,
                                CreatedBy = "Phase1Seeder"
                            };
                            db.ManagementCustomPositions.Add(custPos);
                            await db.SaveChangesAsync(ct);
                        }
                        mmByName.CustomPositionId = custPos.Id;
                    }
                    await db.SaveChangesAsync(ct);
                    return existingByName;
                }

                // Create new staff record
                StaffMember newStaff;
                if (isManagement && deptId == null)
                {
                    int? customPosId = null;
                    if (!string.IsNullOrWhiteSpace(jobTitle))
                    {
                        var custPosNorm = TextNormalizer.NameKey(jobTitle);
                        var custPos = await db.ManagementCustomPositions.FirstOrDefaultAsync(x => x.NormalizedTitle == custPosNorm, ct);
                        if (custPos == null)
                        {
                            custPos = new ManagementCustomPosition
                            {
                                Title = jobTitle,
                                NormalizedTitle = custPosNorm,
                                CreatedAt = DateTime.UtcNow,
                                CreatedBy = "Phase1Seeder"
                            };
                            db.ManagementCustomPositions.Add(custPos);
                            await db.SaveChangesAsync(ct);
                        }
                        customPosId = custPos.Id;
                    }

                    newStaff = new ManagementMember
                    {
                        FullName = TextNormalizer.CleanText(name),
                        NormalizedName = nameKey,
                        JobTitle = jobTitle,
                        CustomPositionId = customPosId,
                        ContactNumber = cleanPhone,
                        NormalizedContact = phoneKey,
                        IsActive = true,
                        Notes = notes,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                }
                else
                {
                    newStaff = new Teacher
                    {
                        FullName = TextNormalizer.CleanText(name),
                        NormalizedName = nameKey,
                        JobTitle = jobTitle,
                        ContactNumber = cleanPhone,
                        NormalizedContact = phoneKey,
                        DepartmentId = deptId,
                        IsActive = true,
                        Notes = notes,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                }

                db.StaffMembers.Add(newStaff);
                await db.SaveChangesAsync(ct);
                return newStaff;
            }

            // 4. Helper to Find or Create StaffPosition
            async Task<StaffPosition> FindOrCreatePositionAsync(string title, int tierLevel, string unitName, LeadershipRoleType roleType, string? defaultGradeRange = null, string? desc = null)
            {
                var normTitle = TextNormalizer.NameKey(title);
                var tier = tierEntities[tierLevel];
                var unit = unitEntities.TryGetValue(unitName, out var u) ? u : null;

                var position = await db.StaffPositions.FirstOrDefaultAsync(p => p.NormalizedTitle == normTitle && p.TierId == tier.Id && p.OrgUnitId == (unit != null ? unit.Id : (int?)null), ct);
                if (position == null)
                {
                    position = new StaffPosition
                    {
                        Title = title,
                        NormalizedTitle = normTitle,
                        TierId = tier.Id,
                        OrgUnitId = unit?.Id,
                        RoleType = roleType,
                        DefaultGradeRange = defaultGradeRange,
                        Description = desc ?? title,
                        SortOrder = 1,
                        IsActive = true,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                    db.StaffPositions.Add(position);
                    await db.SaveChangesAsync(ct);
                }
                return position;
            }

            // 5. Helper to Assign Staff to Position
            async Task AssignPositionAsync(StaffMember staff, StaffPosition pos, string? gradeRange = null, bool isPrimary = true, string? notes = null, StaffMember? reportsTo = null)
            {
                var existing = await db.StaffPositionAssignments.FirstOrDefaultAsync(a =>
                    a.StaffMemberId == staff.Id &&
                    a.PositionId == pos.Id &&
                    a.OrgUnitId == pos.OrgUnitId &&
                    a.GradeRange == (gradeRange ?? pos.DefaultGradeRange) &&
                    a.IsCurrent, ct);

                if (existing != null)
                {
                    if (notes != null && existing.Notes != notes)
                    {
                        existing.Notes = notes;
                        await db.SaveChangesAsync(ct);
                    }
                    return;
                }

                var assignment = new StaffPositionAssignment
                {
                    StaffMemberId = staff.Id,
                    PositionId = pos.Id,
                    OrgUnitId = pos.OrgUnitId,
                    GradeRange = gradeRange ?? pos.DefaultGradeRange,
                    RoleType = pos.RoleType,
                    IsPrimary = isPrimary,
                    IsCurrent = true,
                    StartDate = new DateOnly(2026, 9, 1),
                    ReportsToStaffMemberId = reportsTo?.Id,
                    Notes = notes,
                    AssignedBy = "System Import (Royal Kings 2026/2027)",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                db.StaffPositionAssignments.Add(assignment);
                await db.SaveChangesAsync(ct);
            }

            // ============================================================
            // SEED LEVEL 1: Executive Advisory & Governance Tier
            // ============================================================
            var principal = await FindOrCreateStaffAsync("Dr. Noha Osama", "01111219254", "School Principal / Head of School", isManagement: true);
            var posPrincipal = await FindOrCreatePositionAsync("School Principal / Head of School", 1, "Executive Advisory & Governance", LeadershipRoleType.Executive);
            await AssignPositionAsync(principal, posPrincipal, isPrimary: true);

            var consultant = await FindOrCreateStaffAsync("Dr. Reham Gamil", "01009602017", "School Consultant", isManagement: true);
            var posConsultant = await FindOrCreatePositionAsync("School Consultant", 1, "Executive Advisory & Governance", LeadershipRoleType.Executive);
            await AssignPositionAsync(consultant, posConsultant, isPrimary: true, reportsTo: principal);

            var deputyHead = await FindOrCreateStaffAsync("Mr. Mohamed Abdul-Rasoul", "01004748355", "School Deputy Headmaster", isManagement: true);
            var posDeputyHead = await FindOrCreatePositionAsync("School Deputy Headmaster", 1, "Executive Advisory & Governance", LeadershipRoleType.Executive);
            await AssignPositionAsync(deputyHead, posDeputyHead, isPrimary: true, reportsTo: principal);

            var dean = await FindOrCreateStaffAsync("Dr. Rania Moh. Hijazi", "01020975325", "Academic Dean", isManagement: true);
            var posDean = await FindOrCreatePositionAsync("Academic Dean", 1, "Executive Advisory & Governance", LeadershipRoleType.Executive);
            await AssignPositionAsync(dean, posDean, isPrimary: true, reportsTo: principal);

            var qaManager = await FindOrCreateStaffAsync("Ms. Nour Aladawy", "01100626917", "Quality Assurance Manager", isManagement: true);
            var posQa = await FindOrCreatePositionAsync("Quality Assurance Manager", 1, "Executive Advisory & Governance", LeadershipRoleType.Executive);
            await AssignPositionAsync(qaManager, posQa, isPrimary: true, reportsTo: principal);

            var asstPrincipal = await FindOrCreateStaffAsync("Farah Akram", "01061182914", "Assistant Principal", isManagement: true);
            var posAsstPrincipal = await FindOrCreatePositionAsync("Assistant Principal", 1, "Executive Advisory & Governance", LeadershipRoleType.Executive);
            await AssignPositionAsync(asstPrincipal, posAsstPrincipal, isPrimary: true, reportsTo: principal);

            // ============================================================
            // SEED LEVEL 2: Academic Leadership Tier
            // ============================================================
            var acadDeputy = await FindOrCreateStaffAsync("Ms. Wessam Alaa", "01020316559", "Academic Deputy", isManagement: true);
            var posAcadDeputy = await FindOrCreatePositionAsync("Academic Deputy", 2, "Academic Leadership", LeadershipRoleType.AcademicLeadership);
            await AssignPositionAsync(acadDeputy, posAcadDeputy, isPrimary: true, reportsTo: dean);

            // Ms. Basma Farghali holds TWO separate positions!
            var basma = await FindOrCreateStaffAsync("Ms. Basma Farghali", "01067103468", "Academic Head (Grades 2–10)", isManagement: true);
            var posAcadHead = await FindOrCreatePositionAsync("Academic Head (Grades 2–10)", 2, "Academic Leadership", LeadershipRoleType.AcademicLeadership, "Grades 2–10");
            var posHeadStages = await FindOrCreatePositionAsync("Head of Stages (Grades 2–6)", 2, "Academic Leadership", LeadershipRoleType.AcademicLeadership, "Grades 2–6");
            await AssignPositionAsync(basma, posAcadHead, gradeRange: "Grades 2–10", isPrimary: true, reportsTo: acadDeputy);
            await AssignPositionAsync(basma, posHeadStages, gradeRange: "Grades 2–6", isPrimary: false, reportsTo: acadDeputy);

            // Ms. Eman Gooda holds Academic LMS Reviser (Level 2) and Team Leader Grades 4-10 (Level 6)
            var emanGooda = await FindOrCreateStaffAsync("Ms. Eman Gooda", "01028355886", "Academic LMS Reviser", isManagement: false, deptId: 2);
            var posLmsReviser = await FindOrCreatePositionAsync("Academic LMS Reviser", 2, "Academic Leadership", LeadershipRoleType.AcademicLeadership);
            await AssignPositionAsync(emanGooda, posLmsReviser, isPrimary: true, reportsTo: acadDeputy);

            var hrLeader = await FindOrCreateStaffAsync("Ms. Maha Ismael", "01067823339", "Human Resources (HR)", isManagement: true);
            var posHr = await FindOrCreatePositionAsync("Human Resources (HR)", 2, "Academic Leadership", LeadershipRoleType.AcademicLeadership);
            await AssignPositionAsync(hrLeader, posHr, isPrimary: true, reportsTo: principal);

            // ============================================================
            // SEED LEVEL 3: Parents Care, PR & Counseling Tier
            // ============================================================
            var martha = await FindOrCreateStaffAsync("Ms. Martha", "01277876226", "Parents Care");
            var posMartha = await FindOrCreatePositionAsync("Parents Care", 3, "Parents Care, PR & Counseling", LeadershipRoleType.Support);
            await AssignPositionAsync(martha, posMartha, isPrimary: true);

            var marwaSafwat = await FindOrCreateStaffAsync("Ms. Marwa Safwat", "01001224414", "Public Relations (PR)");
            var posPr = await FindOrCreatePositionAsync("Public Relations (PR)", 3, "Parents Care, PR & Counseling", LeadershipRoleType.Specialist);
            await AssignPositionAsync(marwaSafwat, posPr, isPrimary: true);

            // Ms. Merna Ahmed holds Admissions Tour Specialist AND School Receptionist
            var mernaAhmed = await FindOrCreateStaffAsync("Ms. Merna Ahmed", "01274961909", "Admissions Tour Specialist");
            var posAdmissions = await FindOrCreatePositionAsync("Admissions Tour Specialist", 3, "Parents Care, PR & Counseling", LeadershipRoleType.Specialist);
            var posReceptionist = await FindOrCreatePositionAsync("School Receptionist", 3, "Parents Care, PR & Counseling", LeadershipRoleType.Support);
            await AssignPositionAsync(mernaAhmed, posAdmissions, isPrimary: true);
            await AssignPositionAsync(mernaAhmed, posReceptionist, isPrimary: false);

            var manar = await FindOrCreateStaffAsync("Ms. Manar", "01127198157", "School Receptionist");
            await AssignPositionAsync(manar, posReceptionist, isPrimary: true);

            var maiAhmed = await FindOrCreateStaffAsync("Ms. Mai Ahmed", "01014654606", "School Call Center Agent (Complaint)");
            var posCallCenter = await FindOrCreatePositionAsync("School Call Center Agent (Complaint)", 3, "Parents Care, PR & Counseling", LeadershipRoleType.Support);
            await AssignPositionAsync(maiAhmed, posCallCenter, isPrimary: true);

            var sajaAhmed = await FindOrCreateStaffAsync("Ms. Saja Ahmed", "01007298812", "School Psychological");
            var posPsych = await FindOrCreatePositionAsync("School Psychological", 3, "Parents Care, PR & Counseling", LeadershipRoleType.Specialist);
            await AssignPositionAsync(sajaAhmed, posPsych, isPrimary: true);

            var marianSoliman = await FindOrCreateStaffAsync("Ms. Marian Soliman", "01023585017", "Discipline Officer Department");
            var posDiscipline = await FindOrCreatePositionAsync("Discipline Officer Department", 3, "Parents Care, PR & Counseling", LeadershipRoleType.Specialist);
            await AssignPositionAsync(marianSoliman, posDiscipline, isPrimary: true);

            // ============================================================
            // SEED LEVEL 4: Floor Supervisors
            // ============================================================
            var posFloorSup = await FindOrCreatePositionAsync("Floor Supervisor", 4, "Floor Supervisors", LeadershipRoleType.FloorSupervisor);

            var doniaMostafa = await FindOrCreateStaffAsync("Ms. Donia Mostafa", "01145309275", "Floor Supervisor");
            await AssignPositionAsync(doniaMostafa, posFloorSup, isPrimary: true);

            // Note: Mr. Mahmoud Brakat has phone 01025172837 in scanned sheet
            var mahmoudBrakat = await FindOrCreateStaffAsync("Mr. Mahmoud Brakat", "01025172837", "Floor Supervisor", notes: "Shared phone number with operational staff in scanned sheet — flagged for verification");
            await AssignPositionAsync(mahmoudBrakat, posFloorSup, isPrimary: true, notes: "Shared phone number with operational staff in scanned sheet — flagged for verification");

            var ashrafSultan = await FindOrCreateStaffAsync("Mr. Ashraf Sultan", "01102277491", "Floor Supervisor");
            await AssignPositionAsync(ashrafSultan, posFloorSup, isPrimary: true);

            var bassantMamdouh = await FindOrCreateStaffAsync("Ms. Bassant Mamdouh", "01000300977", "Floor Supervisor");
            await AssignPositionAsync(bassantMamdouh, posFloorSup, isPrimary: true);

            // ============================================================
            // SEED ROW 18: Unclear Data (PART 6)
            // ============================================================
            var nourTarek = await FindOrCreateStaffAsync("Mr. Nour Tarek", "01020492629", "Position requires confirmation", notes: "Position requires confirmation");
            var posNourTarek = await FindOrCreatePositionAsync("Position requires confirmation", 4, "Floor Supervisors", LeadershipRoleType.None, desc: "Position unreadable in reference document - requires confirmation");
            await AssignPositionAsync(nourTarek, posNourTarek, isPrimary: true, notes: "Position requires confirmation");

            // ============================================================
            // SEED LEVEL 5: Administrative & Operational Tier
            // ============================================================
            // Mohamed Abdelrasol & Yusuf Shabaan across 3 units
            var mohamedAbdelrasol = await FindOrCreateStaffAsync("Mr. Mohamed Abdelrasol", "01025172837", "Operations & Student Conduct Manager", notes: "Assigned across 3 operational units");
            var yusufShabaan = await FindOrCreateStaffAsync("Mr. Yusuf Shabaan", "01225569068", "Operations & Facilities Coordinator", notes: "Assigned across 3 operational units");

            var posOpsConduct = await FindOrCreatePositionAsync("Operations & Student Conduct Officer", 5, "Operations & Student Conduct Department", LeadershipRoleType.OperationsAdmin);
            await AssignPositionAsync(mohamedAbdelrasol, posOpsConduct, isPrimary: true);
            await AssignPositionAsync(yusufShabaan, posOpsConduct, isPrimary: true);

            var posFacInfra = await FindOrCreatePositionAsync("Facilities & Infrastructure Officer", 5, "Facilities & Infrastructure Unit", LeadershipRoleType.OperationsAdmin);
            await AssignPositionAsync(mohamedAbdelrasol, posFacInfra, isPrimary: false);
            await AssignPositionAsync(yusufShabaan, posFacInfra, isPrimary: false);

            var posSafetySec = await FindOrCreatePositionAsync("Campus Safety & Security Officer", 5, "Campus Safety & Security Unit", LeadershipRoleType.OperationsAdmin);
            await AssignPositionAsync(mohamedAbdelrasol, posSafetySec, isPrimary: false);
            await AssignPositionAsync(yusufShabaan, posSafetySec, isPrimary: false);

            // Ensure Mahmoud Brakat does not have Level 5 operational assignments (which belong to Mohamed Abdelrasol)
            if (mahmoudBrakat.Id != mohamedAbdelrasol.Id)
            {
                var wrongOps = await db.StaffPositionAssignments
                    .Include(a => a.Position)
                    .Where(a => a.StaffMemberId == mahmoudBrakat.Id && a.Position.TierId == 5)
                    .ToListAsync(ct);
                if (wrongOps.Count > 0)
                {
                    db.StaffPositionAssignments.RemoveRange(wrongOps);
                    await db.SaveChangesAsync(ct);
                }
            }

            var itWaleed = await FindOrCreateStaffAsync("Mr. Waleed", "01112188998", "IT & Logistics Specialist");
            var posItLog = await FindOrCreatePositionAsync("IT & Logistics Units", 5, "IT & Logistics Units", LeadershipRoleType.OperationsAdmin);
            await AssignPositionAsync(itWaleed, posItLog, isPrimary: true);

            var posLmsSupport = await FindOrCreatePositionAsync("LMS Support", 5, "LMS Support", LeadershipRoleType.Support);
            await AssignPositionAsync(marwaSafwat, posLmsSupport, isPrimary: false); // Marwa Safwat 2nd assignment

            var manalAbdelraouf = await FindOrCreateStaffAsync("Ms. Manal Abdelraouf", "01008484747", "LMS Support");
            await AssignPositionAsync(manalAbdelraouf, posLmsSupport, isPrimary: true);

            var samarMohsen = await FindOrCreateStaffAsync("Ms. Samar Mohsen", "01283325410", "Copy Center Technician / Operator");
            var posCopyCenter = await FindOrCreatePositionAsync("Copy Center Technician / Operator", 5, "Copy Center", LeadershipRoleType.Operational);
            await AssignPositionAsync(samarMohsen, posCopyCenter, isPrimary: true);

            var posBookstore = await FindOrCreatePositionAsync("Bookstore & Student Distribution Coordinator", 5, "Bookstore & Student Distribution", LeadershipRoleType.Operational);
            await AssignPositionAsync(doniaMostafa, posBookstore, isPrimary: false); // Donia 2nd assignment

            var saraMostafa = await FindOrCreateStaffAsync("Ms. Sara Mostafa", "01096247514", "Bookstore & Student Distribution Coordinator");
            await AssignPositionAsync(saraMostafa, posBookstore, isPrimary: true);

            var posStorage = await FindOrCreatePositionAsync("Storage Room / Inventory Clerk", 5, "Storage Room / Inventory", LeadershipRoleType.Operational);
            await AssignPositionAsync(doniaMostafa, posStorage, isPrimary: false); // Donia 3rd assignment

            var drNadaHashish = await FindOrCreateStaffAsync("Dr. Nada Hashish", "01008383056", "Inclusion Department Specialist");
            var mahmoudKhalil = await FindOrCreateStaffAsync("Mr. Mahmoud Khalil", "01098782036", "Inclusion Department Specialist");
            var posInclusion = await FindOrCreatePositionAsync("Inclusion Department Specialist", 5, "Inclusion Department", LeadershipRoleType.Specialist);
            await AssignPositionAsync(drNadaHashish, posInclusion, isPrimary: true);
            await AssignPositionAsync(mahmoudKhalil, posInclusion, isPrimary: true);

            var posFacMaint = await FindOrCreatePositionAsync("Facilities & Maintenance Supervisor", 5, "Facilities & Maintenance", LeadershipRoleType.OperationsAdmin);
            await AssignPositionAsync(doniaMostafa, posFacMaint, isPrimary: false); // Donia 4th assignment

            // ============================================================
            // SEED PART 3: Academic Department Leadership (Page 3 of PDF)
            // ============================================================

            // 1. English Department (English – ESS – CB)
            var saritha = await FindOrCreateStaffAsync("Ms. Saritha", "01091170531", "English Team Leader — Grades 1–3", deptId: 2);
            var posEngTl1 = await FindOrCreatePositionAsync("Team Leader — Grades 1–3", 6, "English Department (English – ESS – CB)", LeadershipRoleType.TeamLeader, "Grades 1–3");
            await AssignPositionAsync(saritha, posEngTl1, gradeRange: "Grades 1–3", isPrimary: true);

            var posEngTl2 = await FindOrCreatePositionAsync("Team Leader — Grades 4–10", 6, "English Department (English – ESS – CB)", LeadershipRoleType.TeamLeader, "Grades 4–10");
            await AssignPositionAsync(emanGooda, posEngTl2, gradeRange: "Grades 4–10", isPrimary: false);

            // 2. Math Department
            var mamdouhAwaad = await FindOrCreateStaffAsync("Mr. Mamdouh Awaad", "01201725151", "Math HOD — PreK to Grade 9", deptId: 3);
            var posMathHod = await FindOrCreatePositionAsync("HOD — PreK to Grade 9", 6, "Math Department", LeadershipRoleType.HOD, "PreK to Grade 9");
            await AssignPositionAsync(mamdouhAwaad, posMathHod, gradeRange: "PreK to Grade 9", isPrimary: true);

            var mohamedAliMath = await FindOrCreateStaffAsync("Mr. Mohamed Ali", "01112191030", "Math Coordinator — Grades 2–9", deptId: 3, notes: "Math Coordinator (phone: 01112191030)");
            var posMathCoord = await FindOrCreatePositionAsync("Math Coordinator — Grades 2–9", 6, "Math Department", LeadershipRoleType.Coordinator, "Grades 2–9");
            await AssignPositionAsync(mohamedAliMath, posMathCoord, gradeRange: "Grades 2–9", isPrimary: true);

            var salmaAwaad = await FindOrCreateStaffAsync("Ms. Salma Awaad", "01125038577", "Math Team Leader — PreK to Grade 1", deptId: 3);
            var posMathTl = await FindOrCreatePositionAsync("Team Leader — PreK to Grade 1", 6, "Math Department", LeadershipRoleType.TeamLeader, "PreK to Grade 1");
            await AssignPositionAsync(salmaAwaad, posMathTl, gradeRange: "PreK to Grade 1", isPrimary: true);

            // 3. Arabic & Religion Department
            var emanAhmed = await FindOrCreateStaffAsync("Ms. Eman Ahmed", "01101272543", "Arabic & Religion Team Leader — PreK to G3", deptId: 1);
            var posArbRelTl = await FindOrCreatePositionAsync("Team Leaders — PreK to G3", 6, "Arabic & Religion Department", LeadershipRoleType.TeamLeader, "PreK to G3");
            await AssignPositionAsync(emanAhmed, posArbRelTl, gradeRange: "PreK to G3", isPrimary: true);

            // 4. Arabic Social Studies
            var fatmaSalah = await FindOrCreateStaffAsync("Ms. Fatma Salah", "01009149132", "Arabic Social Studies HOD", deptId: 5);
            var posAssHod = await FindOrCreatePositionAsync("HOD (Arabic Social Studies)", 6, "Arabic Social Studies", LeadershipRoleType.HOD);
            await AssignPositionAsync(fatmaSalah, posAssHod, isPrimary: true);

            // 5. French Department
            var enjySalah = await FindOrCreateStaffAsync("Ms. Enjy Salah", "01005645393", "French HOD", deptId: 13);
            var posFrenchHod = await FindOrCreatePositionAsync("HOD (French)", 6, "French Department", LeadershipRoleType.HOD);
            await AssignPositionAsync(enjySalah, posFrenchHod, isPrimary: true);

            // 6. German Department (Two separate HOD assignments!)
            var habibaEssam = await FindOrCreateStaffAsync("Ms. Habiba Essam", "01146642303", "German HOD — Grades 4–10", deptId: 13);
            var posGerHod1 = await FindOrCreatePositionAsync("HOD — Grades 4–10", 6, "German Department", LeadershipRoleType.HOD, "Grades 4–10");
            await AssignPositionAsync(habibaEssam, posGerHod1, gradeRange: "Grades 4–10", isPrimary: true);

            var mohamedAliGerman = await FindOrCreateStaffAsync("Mr. Mohamed Ali", "01032853184", "German HOD — PreK to Grade 3", deptId: 13, notes: "German HOD (phone: 01032853184) - distinct person from Math Coordinator");
            var posGerHod2 = await FindOrCreatePositionAsync("HOD — PreK to Grade 3", 6, "German Department", LeadershipRoleType.HOD, "PreK to Grade 3");
            await AssignPositionAsync(mohamedAliGerman, posGerHod2, gradeRange: "PreK to Grade 3", isPrimary: true);

            await tx.CommitAsync(ct);
            return ServiceResult.Ok();
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            return ServiceResult.Fail("", $"Seeding failed: {ex.Message}");
        }
    }
}
