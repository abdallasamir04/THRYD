namespace SchoolManagement.Web.Services.Ptc;

public sealed class PtcAccessDeniedException() : UnauthorizedAccessException(PtcAccess.DeniedMessage);

/// <summary>
/// The authorization boundary of the PTC module. Phase 1: only the Super Admin (<see cref="ICurrentUser.IsSuperAdmin"/>)
/// may create or change PTC events, schedules, Rounds, substitute assignments or bookings. No staff permission is granted
/// and there is no public or parent access yet: authorized PTC administrators, teachers and parent booking links are added
/// here by later phases (parents will be identified by their booking link, not by "Working as").
/// </summary>
public interface IPtcAccess
{
    /// <summary>True when the person working in this request may manage PTC.</summary>
    bool CanManage { get; }

    void Ensure()
    {
        if (!CanManage) throw new PtcAccessDeniedException();
    }
}

public sealed class PtcAccess(ICurrentUser currentUser) : IPtcAccess
{
    public const string DeniedMessage = "Only the Super Admin can manage Parent-Teacher Conferences.";

    public bool CanManage => currentUser.IsSuperAdmin;
}
