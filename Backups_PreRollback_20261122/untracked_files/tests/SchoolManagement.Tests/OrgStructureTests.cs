using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Tests;

/// <summary>
/// Phase 1: Complete School Administrative, Management, Operational, and Academic Leadership Structure tests.
/// Tests seed verification, hierarchy building, multiple position assignments, grade ranges, and data integrity.
/// </summary>
[Collection("db")]
public class OrgStructureTests(DbFixture fx)
{
    [Fact]
    public async Task Phase1_Seeder_Populates_All_Royal_Kings_Tiers_Units_Positions_And_Assignments_Idempotently()
    {
        await using var db = fx.NewContext();
        var service = DbFixture.OrgStructure(db);

        // Run seed
        var result = await service.SeedPhase1RoyalKingsStructureAsync();
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Value)));

        // Verify Tiers
        var tiers = await service.GetTiersAsync();
        Assert.Equal(6, tiers.Count);
        Assert.Contains(tiers, t => t.TierLevel == 1 && t.Name.Contains("Executive"));
        Assert.Contains(tiers, t => t.TierLevel == 2 && t.Name.Contains("Academic Leadership"));
        Assert.Contains(tiers, t => t.TierLevel == 3 && t.Name.Contains("Parents Care"));
        Assert.Contains(tiers, t => t.TierLevel == 4 && t.Name.Contains("Floor Supervisors"));
        Assert.Contains(tiers, t => t.TierLevel == 5 && t.Name.Contains("Administrative & Operational"));
        Assert.Contains(tiers, t => t.TierLevel == 6 && t.Name.Contains("Academic Department Leadership"));

        // Verify Units count
        var units = await service.GetUnitsAsync();
        Assert.True(units.Count >= 20, $"Expected at least 20 units, got {units.Count}");

        // Verify Key Positions
        var positions = await service.GetPositionsAsync(new PositionQuery { Status = "all" });
        Assert.True(positions.Count >= 25, $"Expected at least 25 positions, got {positions.Count}");

        // 1. Executive Tier (Level 1)
        var principalPos = positions.FirstOrDefault(p => p.Title == "School Principal / Head of School");
        Assert.NotNull(principalPos);
        Assert.Equal(LeadershipRoleType.Executive, principalPos.RoleType);
        Assert.Contains(principalPos.CurrentHolders, h => h.StaffFullName == "Dr. Noha Osama" && h.ContactNumber == "01111219254");

        var consultantPos = positions.FirstOrDefault(p => p.Title == "School Consultant");
        Assert.NotNull(consultantPos);
        Assert.Contains(consultantPos.CurrentHolders, h => h.StaffFullName == "Dr. Reham Gamil" && h.ContactNumber == "01009602017");

        var deputyPos = positions.FirstOrDefault(p => p.Title == "School Deputy Headmaster");
        Assert.NotNull(deputyPos);
        Assert.Contains(deputyPos.CurrentHolders, h => h.StaffFullName == "Mr. Mohamed Abdul-Rasoul" && h.ContactNumber == "01004748355");

        var deanPos = positions.FirstOrDefault(p => p.Title == "Academic Dean");
        Assert.NotNull(deanPos);
        Assert.Contains(deanPos.CurrentHolders, h => h.StaffFullName == "Dr. Rania Moh. Hijazi" && h.ContactNumber == "01020975325");

        // 2. Dual Positions: Ms. Basma Farghali
        var basmaStaff = await db.StaffMembers.FirstOrDefaultAsync(s => s.FullName == "Ms. Basma Farghali");
        Assert.NotNull(basmaStaff);
        var basmaAssignments = await service.GetAssignmentsAsync(staffId: basmaStaff.Id);
        Assert.Equal(2, basmaAssignments.Count);
        Assert.Contains(basmaAssignments, a => a.PositionTitle.Contains("Academic Head") && a.GradeRange == "Grades 2–10");
        Assert.Contains(basmaAssignments, a => a.PositionTitle.Contains("Head of Stages") && a.GradeRange == "Grades 2–6");

        // 3. Dual Positions: Ms. Merna Ahmed
        var mernaStaff = await db.StaffMembers.FirstOrDefaultAsync(s => s.FullName == "Ms. Merna Ahmed");
        Assert.NotNull(mernaStaff);
        var mernaAssignments = await service.GetAssignmentsAsync(staffId: mernaStaff.Id);
        Assert.Equal(2, mernaAssignments.Count);
        Assert.Contains(mernaAssignments, a => a.PositionTitle == "Admissions Tour Specialist");
        Assert.Contains(mernaAssignments, a => a.PositionTitle == "School Receptionist");

        // 4. Quadruple Positions: Ms. Donia Mostafa
        var doniaStaff = await db.StaffMembers.FirstOrDefaultAsync(s => s.FullName == "Ms. Donia Mostafa");
        Assert.NotNull(doniaStaff);
        var doniaAssignments = await service.GetAssignmentsAsync(staffId: doniaStaff.Id);
        Assert.True(doniaAssignments.Count >= 3, $"Expected at least 3 assignments for Donia Mostafa, got {doniaAssignments.Count}");
        Assert.Contains(doniaAssignments, a => a.PositionTitle == "Floor Supervisor");
        Assert.Contains(doniaAssignments, a => a.PositionTitle == "Bookstore & Student Distribution Coordinator");
        Assert.Contains(doniaAssignments, a => a.PositionTitle == "Storage Room / Inventory Clerk");

        // 5. Dual Positions: Ms. Eman Gooda
        var emanStaff = await db.StaffMembers.FirstOrDefaultAsync(s => s.FullName == "Ms. Eman Gooda");
        Assert.NotNull(emanStaff);
        var emanAssignments = await service.GetAssignmentsAsync(staffId: emanStaff.Id);
        Assert.Equal(2, emanAssignments.Count);
        Assert.Contains(emanAssignments, a => a.PositionTitle == "Academic LMS Reviser");
        Assert.Contains(emanAssignments, a => a.PositionTitle.Contains("Team Leader") && a.GradeRange == "Grades 4–10");

        // 6. Confirmation Required: Mr. Nour Tarek (Row 18)
        var nourStaff = await db.StaffMembers.FirstOrDefaultAsync(s => s.FullName == "Mr. Nour Tarek");
        Assert.NotNull(nourStaff);
        Assert.Equal("01020492629", nourStaff.ContactNumber);
        var nourAssignments = await service.GetAssignmentsAsync(staffId: nourStaff.Id);
        Assert.NotEmpty(nourAssignments);
        var nourAss = nourAssignments.First();
        Assert.True(nourAss.RequiresConfirmation);
        Assert.Contains("requires confirmation", nourAss.Notes, StringComparison.OrdinalIgnoreCase);

        // 7. Disambiguation: Two distinct Mohamed Ali records
        var mohamedAlis = await db.StaffMembers.Where(s => s.FullName == "Mr. Mohamed Ali").ToListAsync();
        Assert.Equal(2, mohamedAlis.Count);
        Assert.Contains(mohamedAlis, m => m.ContactNumber == "01112191030"); // Math Coordinator
        Assert.Contains(mohamedAlis, m => m.ContactNumber == "01032853184"); // German HOD

        // 8. Floor Supervisors
        var floorSupervisors = await service.GetAssignmentsAsync(unitId: null);
        var fsList = floorSupervisors.Where(a => a.RoleType == LeadershipRoleType.FloorSupervisor).ToList();
        Assert.True(fsList.Count >= 4, $"Expected at least 4 floor supervisors, got {fsList.Count}");
        Assert.Contains(fsList, f => f.StaffFullName == "Ms. Donia Mostafa");
        Assert.Contains(fsList, f => f.StaffFullName == "Mr. Mahmoud Brakat");
        Assert.Contains(fsList, f => f.StaffFullName == "Mr. Ashraf Sultan");
        Assert.Contains(fsList, f => f.StaffFullName == "Ms. Bassant Mamdouh");

        // 9. Department Leadership with Grade Ranges
        var mathHod = positions.FirstOrDefault(p => p.Title.Contains("Math HOD") || p.Title.Contains("HOD — PreK to Grade 9"));
        Assert.NotNull(mathHod);
        Assert.Contains(mathHod.CurrentHolders, h => h.StaffFullName == "Mr. Mamdouh Awaad" && h.GradeRange == "PreK to Grade 9");

        var mathCoord = positions.FirstOrDefault(p => p.Title.Contains("Math Coordinator"));
        Assert.NotNull(mathCoord);
        Assert.Contains(mathCoord.CurrentHolders, h => h.StaffFullName == "Mr. Mohamed Ali" && h.GradeRange == "Grades 2–9");

        var germanHodG410 = positions.FirstOrDefault(p => p.Title.Contains("Grades 4–10") && p.OrgUnitName == "German Department");
        Assert.NotNull(germanHodG410);
        Assert.Contains(germanHodG410.CurrentHolders, h => h.StaffFullName == "Ms. Habiba Essam");

        var germanHodPreK3 = positions.FirstOrDefault(p => p.Title.Contains("PreK to Grade 3") && p.OrgUnitName == "German Department");
        Assert.NotNull(germanHodPreK3);
        Assert.Contains(germanHodPreK3.CurrentHolders, h => h.StaffFullName == "Mr. Mohamed Ali" && h.ContactNumber == "01032853184");

        // 10. Idempotency test: second run does not duplicate data
        var initialStaffCount = await db.StaffMembers.CountAsync();
        var initialAssignmentsCount = await db.StaffPositionAssignments.CountAsync();

        var reseedResult = await service.SeedPhase1RoyalKingsStructureAsync();
        Assert.True(reseedResult.Succeeded);

        var afterStaffCount = await db.StaffMembers.CountAsync();
        var afterAssignmentsCount = await db.StaffPositionAssignments.CountAsync();
        Assert.Equal(initialStaffCount, afterStaffCount);
        Assert.Equal(initialAssignmentsCount, afterAssignmentsCount);
    }

    [Fact]
    public async Task OrgChart_Builds_Hierarchy_Tree_And_Identifies_Confirmation_Requirements()
    {
        await using var db = fx.NewContext();
        var service = DbFixture.OrgStructure(db);

        await service.SeedPhase1RoyalKingsStructureAsync();

        var chart = await service.GetOrgChartAsync();
        Assert.NotNull(chart);
        Assert.NotEmpty(chart.RootNodes);
        Assert.True(chart.TotalPositions >= 25);
        Assert.True(chart.TotalStaffInLeadership >= 30);
        Assert.True(chart.PendingConfirmationCount >= 1);

        // Find Mr. Nour Tarek in chart
        OrgChartNodeViewModel? FindNode(IEnumerable<OrgChartNodeViewModel> nodes, string name)
        {
            foreach (var n in nodes)
            {
                if (n.Name == name) return n;
                var found = FindNode(n.Children, name);
                if (found != null) return found;
            }
            return null;
        }

        var nourNode = FindNode(chart.RootNodes, "Mr. Nour Tarek");
        Assert.NotNull(nourNode);
        Assert.True(nourNode.RequiresConfirmation);
    }
}
