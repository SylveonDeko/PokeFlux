using Microsoft.AspNetCore.Mvc;

namespace EeveeCore.Controllers.Common;

/// <summary>
///     Base for the staff endpoints. Each action sets its own rank with <see cref="DashStaffAttribute" />.
/// </summary>
public abstract class AdminControllerBase : PokeFluxUserControllerBase
{
    /// <summary>
    ///     Items per page when none is asked for.
    /// </summary>
    protected const int DefaultPageSize = 25;

    private const int MaxPageSize = 50;
    private const int MaxReasonLength = 500;

    /// <summary>
    ///     Gets the staff member's name for notes, or their ID when the token has no name.
    /// </summary>
    protected string StaffName => UserName ?? UserId.ToString();

    /// <summary>
    ///     Clamps a page to 1 or more and a page size to 1 through 50.
    /// </summary>
    /// <param name="page">The page asked for.</param>
    /// <param name="pageSize">The page size asked for.</param>
    protected static (int Page, int PageSize) Paging(int page, int pageSize) =>
        (Math.Max(1, page), Math.Clamp(pageSize, 1, MaxPageSize));

    /// <summary>
    ///     Trims a reason, returning null when it is empty or too long.
    /// </summary>
    /// <param name="reason">The reason sent.</param>
    protected static string? ReadReason(string? reason) =>
        reason?.Trim() is { Length: > 0 and <= MaxReasonLength } trimmed ? trimmed : null;

    /// <summary>
    ///     The 400 answer for a missing reason.
    /// </summary>
    protected ObjectResult ReasonRequired() =>
        Error(400, $"A reason of up to {MaxReasonLength} characters is required.");
}
