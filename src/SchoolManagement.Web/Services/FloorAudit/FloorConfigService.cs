using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Services;

public interface IFloorConfigService
{
    Task<ManagePage> ManageAsync(string tab, CancellationToken ct = default);
    Task<ServiceResult> SaveFloorAsync(FloorForm form, CancellationToken ct = default);
    Task<ServiceResult> SetFloorActiveAsync(int id, bool active, CancellationToken ct = default);
    Task<ServiceResult> AssignClassroomsAsync(int floorId, IReadOnlyCollection<int> classIds, CancellationToken ct = default);
    Task<ServiceResult> MoveAreaAsync(int areaId, int floorId, CancellationToken ct = default);
    Task<ServiceResult> SetAreaActiveAsync(int areaId, bool active, CancellationToken ct = default);
    Task<ServiceResult> SaveFacilityAsync(FacilityForm form, CancellationToken ct = default);
    Task<ServiceResult> SaveItemAsync(AuditItemForm form, CancellationToken ct = default);
    Task<ServiceResult> SetItemActiveAsync(int id, bool active, CancellationToken ct = default);
    Task<ServiceResult> GrantAccessAsync(AccessForm form, CancellationToken ct = default);
    Task<ServiceResult> EndAccessAsync(int id, CancellationToken ct = default);
    Task<FacilityForm?> FacilityFormAsync(int? id, int? floorId, CancellationToken ct = default);
    Task<ClassroomForm> ClassroomFormAsync(int? floorId, int? areaId, CancellationToken ct = default);
    Task<AuditItemForm?> ItemFormAsync(int? id, CancellationToken ct = default);
    Task<AccessForm> AccessFormAsync(AccessForm? posted, CancellationToken ct = default);
    Task<InspectionHistoryPage> HistoryAsync(InspectionFilter filter, CancellationToken ct = default);
    Task<IReadOnlyList<(InspectionBrief Inspection, IReadOnlyList<InspectionResponseView> Responses)>> HistoryExportAsync(InspectionFilter filter, CancellationToken ct = default);
}

/// <summary>
/// Floor Audit configuration (managers only): floors, which classes are on which floor, facilities, the checklist and
/// who may inspect. Nothing is deleted — floors, areas and items are deactivated and access is ended — so history keeps
/// what it refers to. Every change is written to the Floor Audit trail.
/// </summary>
public sealed class FloorConfigService(SchoolDbContext db, ICurrentUser currentUser, IFloorAuditAccessService access, IFloorAuditService audit,
    IStudentMovementService movements) : IFloorConfigService
{
    public const int ExportLimit = 5000;
    private const string ManagersOnly = "Only a Floor Audit manager can change the floor configuration.";

    private async Task<ServiceResult?> DenyAsync(CancellationToken ct)
    {
        var user = await access.CurrentAsync(ct);
        return user.CanManage && user.HasIdentity ? null : ServiceResult.Fail("", ManagersOnly);
    }

    private void Log(string entity, int? id, int? floorId, string action, string text) => FloorAuditLog.Add(db, currentUser, entity, id, floorId, action, text);

    public async Task<ManagePage> ManageAsync(string tab, CancellationToken ct = default)
    {
        var areas = await audit.AreasAsync(false, ct);
        var floors = await db.Floors.AsNoTracking().OrderBy(f => f.SortOrder).ThenBy(f => f.Name).ToListAsync(ct);
        var assigned = areas.Where(a => a.IsActive && a.ClassId is not null).Select(a => a.ClassId!.Value).ToHashSet();
        var used = await db.FloorInspectionResponses.AsNoTracking().Where(r => r.ItemId != null).GroupBy(r => r.ItemId!.Value)
            .Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var activeClasses = await db.Classes.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .Select(c => new { c.Id, c.Name }).ToListAsync(ct);
        return new ManagePage
        {
            Tab = tab,
            Floors = floors.Select(f => new FloorSummary(f.Id, f.Name, f.SortOrder, f.IsActive,
                areas.Count(a => a.FloorId == f.Id && a.IsActive && a.Kind == FloorAreaKind.Classroom),
                areas.Count(a => a.FloorId == f.Id && a.IsActive && a.Kind == FloorAreaKind.Facility),
                areas.Count(a => a.FloorId == f.Id && !a.IsActive))).ToList(),
            Areas = areas,
            UnassignedClasses = activeClasses.Where(c => !assigned.Contains(c.Id)).Select(c => (c.Id, c.Name)).ToList(),
            Items = (await db.FloorAuditItems.AsNoTracking().OrderBy(i => i.SortOrder).ThenBy(i => i.Name).ToListAsync(ct))
                .Select(i => new AuditItemView(i.Id, i.Name, i.Description, i.AppliesTo, i.ResponseType, i.IsRequired, i.IsActive, i.IsDefault, i.SortOrder,
                    used.GetValueOrDefault(i.Id))).ToList(),
            Access = await db.FloorAuditAccess.AsNoTracking().Where(a => a.EndedAt == null).OrderBy(a => a.Role).ThenBy(a => a.StaffMember.FullName)
                .Select(a => new AccessView(a.Id, a.StaffMemberId, a.StaffMember.FullName, a.StaffMember.JobTitle, a.Role, a.Floor!.Name, a.CanRecordMovements,
                    a.GrantedAt, a.GrantedBy)).ToListAsync(ct),
            ManagementTeam = await db.PositionAssignments.AsNoTracking().Where(p => p.IsCurrent).Select(p => p.Member.FullName).Distinct().OrderBy(n => n).ToListAsync(ct),
            Log = await db.FloorAuditLog.AsNoTracking().Where(l => l.Entity != "Inspection" && l.Entity != "Movement").OrderByDescending(l => l.At).ThenByDescending(l => l.Id).Take(30)
                .Select(l => new AuditLogView(l.Entity, l.EntityId, l.Action, l.Text, l.At, l.By)).ToListAsync(ct),
            OverdueMinutes = await movements.OverdueMinutesAsync(ct),
            User = await access.CurrentAsync(ct),
        };
    }

    // ---------------------------------------------------------------- floors

    public async Task<ServiceResult> SaveFloorAsync(FloorForm form, CancellationToken ct = default)
    {
        if (await DenyAsync(ct) is { } denied) return denied;
        var name = TextNormalizer.CleanText(form.Name);
        if (name.Length == 0) return ServiceResult.Fail("Name", "Enter the floor's name.");
        if (name.Length > SchoolFloor.NameMaxLength) return ServiceResult.Fail("Name", $"Use {SchoolFloor.NameMaxLength} characters or fewer.");
        var key = TextNormalizer.NameKey(name);
        if (await db.Floors.AnyAsync(f => f.NormalizedName == key && f.Id != form.Id, ct)) return ServiceResult.Fail("Name", $"{name} already exists.");
        if (form.SortOrder is < 0 or > 99) return ServiceResult.Fail("SortOrder", "Use an order from 0 to 99.");
        SchoolFloor floor;
        if (form.Id is { } id)
        {
            floor = await db.Floors.FirstOrDefaultAsync(f => f.Id == id, ct) ?? throw new InvalidOperationException();
            Log("Floor", id, id, "Edited", $"Floor “{floor.Name}” → “{name}”, order {floor.SortOrder} → {form.SortOrder ?? floor.SortOrder}.");
        }
        else
        {
            floor = new SchoolFloor { CreatedBy = currentUser.Name, SortOrder = (await db.Floors.MaxAsync(f => (int?)f.SortOrder, ct) ?? -1) + 1 };
            db.Floors.Add(floor);
        }
        floor.Name = name;
        floor.NormalizedName = key;
        if (form.SortOrder is { } order) floor.SortOrder = order;
        floor.UpdatedBy = currentUser.Name;
        await db.SaveChangesAsync(ct);
        if (form.Id is null) { Log("Floor", floor.Id, floor.Id, "Added", $"Floor “{name}” added."); await db.SaveChangesAsync(ct); }
        return ServiceResult.Ok(floor.Id);
    }

    public async Task<ServiceResult> SetFloorActiveAsync(int id, bool active, CancellationToken ct = default)
    {
        if (await DenyAsync(ct) is { } denied) return denied;
        var floor = await db.Floors.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (floor is null) return ServiceResult.Fail("", "This floor no longer exists.");
        if (!active && await db.FloorAreas.AnyAsync(a => a.FloorId == id && a.IsActive, ct))
            return ServiceResult.Fail("", $"{floor.Name} still has active classrooms or facilities. Move or deactivate them first.");
        floor.IsActive = active;
        floor.UpdatedBy = currentUser.Name;
        Log("Floor", id, id, active ? "Activated" : "Deactivated", $"Floor “{floor.Name}” {(active ? "activated" : "deactivated")}.");
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok(id);
    }

    // ---------------------------------------------------------------- classrooms and facilities

    public async Task<ClassroomForm> ClassroomFormAsync(int? floorId, int? areaId, CancellationToken ct = default)
    {
        var page = await ManageAsync("floors", ct);
        var form = new ClassroomForm
        {
            FloorId = floorId, AreaId = areaId,
            Floors = page.Floors.Where(f => f.IsActive).Select(f => (f.Id, f.Name)).ToList(),
            Classes = page.UnassignedClasses,
        };
        if (areaId is { } id && page.Areas.FirstOrDefault(a => a.Id == id) is { } area)
        {
            form.ClassName = area.Name;
            form.FloorId ??= area.FloorId;
        }
        return form;
    }

    public async Task<ServiceResult> AssignClassroomsAsync(int floorId, IReadOnlyCollection<int> classIds, CancellationToken ct = default)
    {
        if (await DenyAsync(ct) is { } denied) return denied;
        var floor = await db.Floors.FirstOrDefaultAsync(f => f.Id == floorId && f.IsActive, ct);
        if (floor is null) return ServiceResult.Fail("FloorId", "Choose an active floor.");
        var ids = classIds.Distinct().ToList();
        if (ids.Count == 0) return ServiceResult.Fail("ClassIds", "Choose at least one class.");
        var classes = await db.Classes.Where(c => ids.Contains(c.Id)).Select(c => new { c.Id, c.Name, c.IsActive }).ToListAsync(ct);
        if (classes.Count != ids.Count) return ServiceResult.Fail("ClassIds", "A chosen class no longer exists. Reload the page.");
        var existing = await db.FloorAreas.Include(a => a.Floor).Where(a => a.SchoolClassId != null && ids.Contains(a.SchoolClassId.Value)).ToListAsync(ct);
        var result = new ServiceResult();
        foreach (var e in existing.Where(e => e.IsActive))
        {
            var name = classes.First(c => c.Id == e.SchoolClassId).Name;
            result.AddError("ClassIds", e.FloorId == floorId
                ? $"{name} is already on {floor.Name}."
                : $"{name} is already on {e.Floor.Name}. Use “Move” on its card to change its floor.");
        }
        foreach (var c in classes.Where(c => !c.IsActive)) result.AddError("ClassIds", $"{c.Name} is not an active class.");
        if (!result.Succeeded) return result;

        var order = await db.FloorAreas.Where(a => a.FloorId == floorId).MaxAsync(a => (int?)a.SortOrder, ct) ?? 0;
        foreach (var c in classes)
        {
            // A class's room is one row for life: re-assigning reactivates it (on this floor), so its history stays with it.
            var area = existing.FirstOrDefault(e => e.SchoolClassId == c.Id);
            if (area is null)
            {
                area = new FloorArea { Kind = FloorAreaKind.Classroom, SchoolClassId = c.Id, CreatedBy = currentUser.Name, SortOrder = ++order };
                db.FloorAreas.Add(area);
            }
            var from = area.Id == 0 ? null : area.Floor?.Name;
            area.FloorId = floorId;
            area.IsActive = true;
            area.UpdatedBy = currentUser.Name;
            await db.SaveChangesAsync(ct);
            Log("Area", area.Id, floorId, "Assigned", from is null || from == floor.Name
                ? $"Classroom {c.Name} assigned to {floor.Name}."
                : $"Classroom {c.Name} assigned to {floor.Name} (previously on {from}; its earlier inspections keep their floor).");
        }
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> MoveAreaAsync(int areaId, int floorId, CancellationToken ct = default)
    {
        if (await DenyAsync(ct) is { } denied) return denied;
        var area = await db.FloorAreas.Include(a => a.Floor).Include(a => a.SchoolClass).FirstOrDefaultAsync(a => a.Id == areaId, ct);
        if (area is null) return ServiceResult.Fail("", "This area no longer exists.");
        var to = await db.Floors.FirstOrDefaultAsync(f => f.Id == floorId && f.IsActive, ct);
        if (to is null) return ServiceResult.Fail("FloorId", "Choose an active floor.");
        if (area.FloorId == floorId) return ServiceResult.Fail("FloorId", $"It is already on {to.Name}.");
        var name = area.SchoolClass?.Name ?? area.Name;
        if (area.NormalizedName is { } key && await db.FloorAreas.AnyAsync(a => a.FloorId == floorId && a.NormalizedName == key, ct))
            return ServiceResult.Fail("FloorId", $"{to.Name} already has a facility named {name}.");
        var from = area.Floor.Name;
        area.FloorId = floorId;
        area.UpdatedBy = currentUser.Name;
        Log("Area", area.Id, floorId, "Moved", $"{name} moved from {from} to {to.Name}. Inspections made on {from} stay recorded there; new rounds use {to.Name}.");
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok(area.Id);
    }

    public async Task<ServiceResult> SetAreaActiveAsync(int areaId, bool active, CancellationToken ct = default)
    {
        if (await DenyAsync(ct) is { } denied) return denied;
        var area = await db.FloorAreas.Include(a => a.Floor).Include(a => a.SchoolClass).FirstOrDefaultAsync(a => a.Id == areaId, ct);
        if (area is null) return ServiceResult.Fail("", "This area no longer exists.");
        if (active && !area.Floor.IsActive) return ServiceResult.Fail("", $"{area.Floor.Name} is inactive. Activate the floor or move the area first.");
        area.IsActive = active;
        area.UpdatedBy = currentUser.Name;
        var name = area.SchoolClass?.Name ?? area.Name;
        Log("Area", area.Id, area.FloorId, active ? "Activated" : "Deactivated",
            active ? $"{name} on {area.Floor.Name} activated." : $"{name} on {area.Floor.Name} deactivated: no new inspections; its history stays.");
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok(area.Id);
    }

    public async Task<FacilityForm?> FacilityFormAsync(int? id, int? floorId, CancellationToken ct = default)
    {
        var floors = await db.Floors.AsNoTracking().Where(f => f.IsActive).OrderBy(f => f.SortOrder).Select(f => new ValueTuple<int, string>(f.Id, f.Name)).ToListAsync(ct);
        if (id is null) return new FacilityForm { FloorId = floorId, Floors = floors };
        var a = await db.FloorAreas.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.Kind == FloorAreaKind.Facility, ct);
        return a is null ? null : new FacilityForm
        {
            Id = a.Id, FloorId = a.FloorId, Name = a.Name, FacilityType = a.FacilityType, Description = a.Description, IsActive = a.IsActive, Floors = floors,
        };
    }

    public async Task<ServiceResult> SaveFacilityAsync(FacilityForm form, CancellationToken ct = default)
    {
        if (await DenyAsync(ct) is { } denied) return denied;
        var result = new ServiceResult();
        var name = TextNormalizer.CleanText(form.Name);
        var description = TextNormalizer.CleanText(form.Description);
        if (name.Length == 0) result.AddError("Name", "Enter the facility's name.");
        else if (name.Length > FloorArea.NameMaxLength) result.AddError("Name", $"Use {FloorArea.NameMaxLength} characters or fewer.");
        if (description.Length > FloorArea.DescriptionMaxLength) result.AddError("Description", $"Use {FloorArea.DescriptionMaxLength} characters or fewer.");
        if (form.FacilityType is not { } type || !Enum.IsDefined(type)) result.AddError("FacilityType", "Choose the facility type.");
        var floor = form.FloorId is { } fid ? await db.Floors.FirstOrDefaultAsync(f => f.Id == fid, ct) : null;
        if (floor is null) result.AddError("FloorId", "Choose the floor.");
        if (!result.Succeeded) return result;
        var key = TextNormalizer.NameKey(name);
        if (await db.FloorAreas.AnyAsync(a => a.FloorId == floor!.Id && a.NormalizedName == key && a.Id != form.Id, ct))
            return ServiceResult.Fail("Name", $"{floor!.Name} already has a facility named {name}.");
        if (form.IsActive && !floor!.IsActive) return ServiceResult.Fail("FloorId", $"{floor.Name} is inactive.");

        FloorArea area;
        string? before = null;
        if (form.Id is { } id)
        {
            area = await db.FloorAreas.Include(a => a.Floor).FirstOrDefaultAsync(a => a.Id == id && a.Kind == FloorAreaKind.Facility, ct)
                   ?? throw new InvalidOperationException("Facility not found.");
            before = $"{area.Name} ({FloorAuditLabels.Facility(area.FacilityType)}, {area.Floor.Name}, {(area.IsActive ? "active" : "inactive")})";
        }
        else
        {
            area = new FloorArea { Kind = FloorAreaKind.Facility, CreatedBy = currentUser.Name };
            area.SortOrder = (await db.FloorAreas.Where(a => a.FloorId == floor!.Id).MaxAsync(a => (int?)a.SortOrder, ct) ?? 0) + 1;
            db.FloorAreas.Add(area);
        }
        area.FloorId = floor!.Id;
        area.Name = name;
        area.NormalizedName = key;
        area.FacilityType = form.FacilityType;
        area.Description = description.Length == 0 ? null : description;
        area.IsActive = form.IsActive;
        area.UpdatedBy = currentUser.Name;
        await db.SaveChangesAsync(ct);
        var after = $"{name} ({FloorAuditLabels.Facility(form.FacilityType)}, {floor.Name}, {(form.IsActive ? "active" : "inactive")})";
        Log("Area", area.Id, floor.Id, before is null ? "Added" : "Edited", before is null ? $"Facility {after} added." : $"Facility {before} → {after}.");
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok(area.Id);
    }

    // ---------------------------------------------------------------- checklist

    public async Task<AuditItemForm?> ItemFormAsync(int? id, CancellationToken ct = default)
    {
        if (id is null) return new AuditItemForm();
        var i = await db.FloorAuditItems.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (i is null) return null;
        return new AuditItemForm
        {
            Id = i.Id, Name = i.Name, Description = i.Description, AppliesTo = i.AppliesTo, ResponseType = i.ResponseType, IsRequired = i.IsRequired,
            IsActive = i.IsActive, SortOrder = i.SortOrder,
            TimesUsed = await db.FloorInspectionResponses.CountAsync(r => r.ItemId == i.Id, ct),
        };
    }

    public async Task<ServiceResult> SaveItemAsync(AuditItemForm form, CancellationToken ct = default)
    {
        if (await DenyAsync(ct) is { } denied) return denied;
        var result = new ServiceResult();
        var name = TextNormalizer.CleanText(form.Name);
        var description = TextNormalizer.CleanText(form.Description);
        if (name.Length == 0) result.AddError("Name", "Enter the item's name.");
        else if (name.Length > FloorAuditItem.NameMaxLength) result.AddError("Name", $"Use {FloorAuditItem.NameMaxLength} characters or fewer.");
        if (description.Length > FloorAuditItem.DescriptionMaxLength) result.AddError("Description", $"Use {FloorAuditItem.DescriptionMaxLength} characters or fewer.");
        if (!Enum.IsDefined(form.AppliesTo)) result.AddError("AppliesTo", "Choose where the item applies.");
        if (!Enum.IsDefined(form.ResponseType)) result.AddError("ResponseType", "Choose the response type.");
        if (form.SortOrder is < 0 or > 9999) result.AddError("SortOrder", "Use an order from 0 to 9999.");
        if (!result.Succeeded) return result;
        var key = TextNormalizer.NameKey(name);
        if (await db.FloorAuditItems.AnyAsync(i => i.NormalizedName == key && i.Id != form.Id, ct))
            return ServiceResult.Fail("Name", $"An item named {name} already exists.");

        FloorAuditItem item;
        string? before = null;
        if (form.Id is { } id)
        {
            item = await db.FloorAuditItems.FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new InvalidOperationException("Item not found.");
            before = Describe(item);
        }
        else
        {
            item = new FloorAuditItem { CreatedBy = currentUser.Name, SortOrder = (await db.FloorAuditItems.MaxAsync(i => (int?)i.SortOrder, ct) ?? 0) + 10 };
            db.FloorAuditItems.Add(item);
        }
        item.Name = name;
        item.NormalizedName = key;
        item.Description = description.Length == 0 ? null : description;
        item.AppliesTo = form.AppliesTo;
        item.ResponseType = form.ResponseType;
        item.IsRequired = form.IsRequired;
        item.IsActive = form.IsActive;
        if (form.SortOrder is { } order) item.SortOrder = order;
        item.UpdatedBy = currentUser.Name;
        await db.SaveChangesAsync(ct);
        Log("Item", item.Id, null, before is null ? "Added" : "Edited",
            before is null ? $"Checklist item added: {Describe(item)}." : $"Checklist item {before} → {Describe(item)}. Inspections already started keep the question as it was.");
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok(item.Id);
    }

    private static string Describe(FloorAuditItem i) =>
        $"“{i.Name}” ({FloorAuditLabels.Scope(i.AppliesTo)}, {AuditResponses.Label(i.ResponseType)}, {(i.IsRequired ? "required" : "optional")}, {(i.IsActive ? "active" : "inactive")})";

    public async Task<ServiceResult> SetItemActiveAsync(int id, bool active, CancellationToken ct = default)
    {
        if (await DenyAsync(ct) is { } denied) return denied;
        var item = await db.FloorAuditItems.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (item is null) return ServiceResult.Fail("", "This item no longer exists.");
        item.IsActive = active;
        item.UpdatedBy = currentUser.Name;
        Log("Item", id, null, active ? "Activated" : "Deactivated", $"Checklist item “{item.Name}” {(active ? "activated" : "deactivated")}; recorded answers are kept.");
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok(id);
    }

    // ---------------------------------------------------------------- access

    public async Task<AccessForm> AccessFormAsync(AccessForm? posted, CancellationToken ct = default)
    {
        var form = posted ?? new AccessForm();
        form.Staff = (await db.StaffMembers.AsNoTracking().OrderBy(s => s.FullName).Select(s => new { s.Id, s.FullName, s.JobTitle }).ToListAsync(ct))
            .Select(s => (s.Id, s.FullName, s.JobTitle)).ToList();
        form.Floors = await db.Floors.AsNoTracking().Where(f => f.IsActive).OrderBy(f => f.SortOrder).Select(f => new ValueTuple<int, string>(f.Id, f.Name)).ToListAsync(ct);
        return form;
    }

    public async Task<ServiceResult> GrantAccessAsync(AccessForm form, CancellationToken ct = default)
    {
        if (await DenyAsync(ct) is { } denied) return denied;
        if (form.StaffId is not { } staffId) return ServiceResult.Fail("StaffId", "Choose the staff member.");
        var staff = await db.StaffMembers.AsNoTracking().Where(s => s.Id == staffId).Select(s => s.FullName).FirstOrDefaultAsync(ct);
        if (staff is null) return ServiceResult.Fail("StaffId", "This staff member no longer exists.");
        if (!Enum.IsDefined(form.Role)) return ServiceResult.Fail("Role", "Choose the role.");
        int? floorId = form.Role == FloorAuditRole.Inspector ? form.FloorId : null;
        string? floorName = null;
        if (floorId is { } fid)
        {
            floorName = await db.Floors.Where(f => f.Id == fid && f.IsActive).Select(f => f.Name).FirstOrDefaultAsync(ct);
            if (floorName is null) return ServiceResult.Fail("FloorId", "Choose an active floor, or every floor.");
        }
        if (await db.FloorAuditAccess.AnyAsync(a => a.StaffMemberId == staffId && a.EndedAt == null && a.Role == form.Role && a.FloorId == floorId, ct))
            return ServiceResult.Fail("StaffId", $"{staff} already has this access.");
        var row = new FloorAuditAccess
        {
            StaffMemberId = staffId, Role = form.Role, FloorId = floorId, CanRecordMovements = form.Role == FloorAuditRole.Manager || form.CanRecordMovements,
            GrantedAt = DateTime.UtcNow, GrantedBy = currentUser.Name,
        };
        db.FloorAuditAccess.Add(row);
        await db.SaveChangesAsync(ct);
        Log("Access", row.Id, floorId, "Granted", form.Role == FloorAuditRole.Manager
            ? $"{staff}: Floor Audit Manager."
            : $"{staff}: Inspector of {floorName ?? "every floor"}{(row.CanRecordMovements ? ", may record student movements" : "")}.");
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok(row.Id);
    }

    public async Task<ServiceResult> EndAccessAsync(int id, CancellationToken ct = default)
    {
        if (await DenyAsync(ct) is { } denied) return denied;
        var row = await db.FloorAuditAccess.Include(a => a.StaffMember).Include(a => a.Floor).FirstOrDefaultAsync(a => a.Id == id && a.EndedAt == null, ct);
        if (row is null) return ServiceResult.Fail("", "This access was already ended.");
        row.EndedAt = DateTime.UtcNow;
        row.EndedBy = currentUser.Name;
        Log("Access", id, row.FloorId, "Ended", $"{row.StaffMember.FullName}: {(row.Role == FloorAuditRole.Manager ? "Floor Audit Manager" : $"Inspector of {row.Floor?.Name ?? "every floor"}")} ended.");
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok(id);
    }

    // ---------------------------------------------------------------- history

    private IQueryable<FloorAreaInspection> Filtered(InspectionFilter f)
    {
        var q = db.FloorInspections.AsNoTracking().Where(i => i.Date >= f.From && i.Date <= f.To);
        if (f.FloorId is { } floor) q = q.Where(i => i.FloorId == floor);
        if (f.AreaId is { } area) q = q.Where(i => i.FloorAreaId == area);
        if (f.SlotId is { } slot) q = q.Where(i => i.TimeSlotId == slot);
        if (f.Round is { } round) q = q.Where(i => i.Round == round);
        if (f.InspectorId is { } who) q = q.Where(i => i.InspectorStaffId == who);
        return f.Status switch
        {
            "completed" => q.Where(i => i.Status == FloorInspectionStatus.Completed),
            "in-progress" => q.Where(i => i.Status == FloorInspectionStatus.InProgress),
            "issues" => q.Where(i => i.IssueCount > 0),
            "no-issues" => q.Where(i => i.Status == FloorInspectionStatus.Completed && i.IssueCount == 0),
            _ => q,
        };
    }

    public async Task<InspectionHistoryPage> HistoryAsync(InspectionFilter filter, CancellationToken ct = default)
    {
        var q = Filtered(filter);
        var total = await q.CountAsync(ct);
        var size = Paging.NormalizePageSize(filter.PageSize);
        var page = Paging.ClampPage(filter.Page, total, size);
        var items = await FloorAuditService.Briefs(q.OrderByDescending(i => i.Date).ThenByDescending(i => i.SessionStart).ThenByDescending(i => i.Round)
            .ThenBy(i => i.AreaName)).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        var all = Filtered(filter with { Status = null });
        return new InspectionHistoryPage
        {
            Filter = filter with { Page = page, PageSize = size },
            Result = new PagedResult<InspectionBrief> { Items = items, Page = page, PageSize = size, TotalCount = total },
            Floors = await db.Floors.AsNoTracking().OrderBy(f => f.SortOrder).Select(f => new ValueTuple<int, string>(f.Id, f.Name)).ToListAsync(ct),
            Areas = await audit.AreasAsync(false, ct),
            Sessions = (await db.FloorInspections.AsNoTracking().Where(i => i.Date >= filter.From && i.Date <= filter.To)
                    .Select(i => new { i.TimeSlotId, i.SessionLabel, i.SessionStart }).Distinct().ToListAsync(ct))
                .GroupBy(s => s.TimeSlotId).Select(g => g.OrderByDescending(x => x.SessionStart).First())
                .OrderBy(s => s.SessionStart).Select(s => (s.TimeSlotId, $"{s.SessionLabel} ({FloorAuditLabels.Time(s.SessionStart)})")).ToList(),
            Inspectors = (await db.FloorInspections.AsNoTracking().Where(i => i.InspectorStaffId != null)
                    .Select(i => new { Id = i.InspectorStaffId!.Value, i.InspectorName }).Distinct().ToListAsync(ct))
                .GroupBy(x => x.Id).Select(g => (g.Key, g.First().InspectorName ?? "—")).OrderBy(x => x.Item2).ToList(),
            Completed = await all.CountAsync(i => i.Status == FloorInspectionStatus.Completed, ct),
            InProgress = await all.CountAsync(i => i.Status == FloorInspectionStatus.InProgress, ct),
            WithIssues = await all.CountAsync(i => i.IssueCount > 0, ct),
        };
    }

    public async Task<IReadOnlyList<(InspectionBrief Inspection, IReadOnlyList<InspectionResponseView> Responses)>> HistoryExportAsync(InspectionFilter filter, CancellationToken ct = default)
    {
        var q = Filtered(filter);
        var briefs = await FloorAuditService.Briefs(q.OrderBy(i => i.Date).ThenBy(i => i.SessionStart).ThenBy(i => i.Round).ThenBy(i => i.AreaName))
            .Take(ExportLimit).ToListAsync(ct);
        var ids = briefs.Select(b => b.Id).ToList();
        var responses = new List<(int InspectionId, InspectionResponseView View, int Order)>();
        foreach (var chunk in ids.Chunk(1000))
            responses.AddRange((await db.FloorInspectionResponses.AsNoTracking().Where(r => chunk.Contains(r.InspectionId))
                    .Select(r => new { r.InspectionId, r.SortOrder, r.ItemName, r.ItemDescription, r.ResponseType, r.IsRequired, r.Value, r.IsIssue }).ToListAsync(ct))
                .Select(r => (r.InspectionId, new InspectionResponseView(r.ItemName, r.ItemDescription, r.ResponseType, r.IsRequired, r.Value, r.IsIssue), r.SortOrder)));
        var byId = responses.GroupBy(r => r.InspectionId).ToDictionary(g => g.Key, g => (IReadOnlyList<InspectionResponseView>)g.OrderBy(r => r.Order).Select(r => r.View).ToList());
        return briefs.Select(b => (b, byId.GetValueOrDefault(b.Id) ?? (IReadOnlyList<InspectionResponseView>)[])).ToList();
    }
}
