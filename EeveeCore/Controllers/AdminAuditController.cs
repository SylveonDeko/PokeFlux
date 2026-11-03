using EeveeCore.Common.Enums;
using EeveeCore.Controllers.Common;
using EeveeCore.Controllers.Models;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Mvc;

namespace EeveeCore.Controllers;

/// <summary>
///     The trail of staff actions taken through the dashboard.
/// </summary>
/// <param name="dbProvider">The database connection provider.</param>
/// <param name="views">Builds the admin shapes.</param>
[Route("api/admin/audit")]
public class AdminAuditController(LinqToDbConnectionProvider dbProvider, AdminViews views) : AdminControllerBase
{
    /// <summary>
    ///     Lists staff actions newest first.
    /// </summary>
    /// <param name="staff">Only actions by this staff member.</param>
    /// <param name="target">Only actions on this user.</param>
    /// <param name="page">The page, from 1.</param>
    /// <param name="pageSize">Entries per page, up to 50.</param>
    [HttpGet]
    [DashStaff(StaffRank.Mod)]
    public async Task<IActionResult> List(ulong? staff = null, ulong? target = null, int page = 1,
        int pageSize = DefaultPageSize)
    {
        (page, pageSize) = Paging(page, pageSize);

        await using var db = await dbProvider.GetConnectionAsync();
        var query = db.StaffAuditLog.AsQueryable();
        if (staff is { } staffId)
            query = query.Where(e => e.StaffId == staffId);
        if (target is { } targetId)
            query = query.Where(e => e.TargetUserId == targetId);

        var total = await query.CountAsync();
        var entries = await query
            .OrderByDescending(e => e.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var names = await views.NamesAsync(entries.Where(e => e.TargetUserId is not null)
            .Select(e => e.TargetUserId!.Value));
        var items = entries.Select(e => new AdminAuditEntry(e.Id, new AdminUserRef(e.StaffId, e.StaffName), e.Action,
                e.TargetUserId is { } t ? AdminViews.Ref(t, names) : null, e.Details, e.Reason, e.CreatedAt))
            .ToList();
        return Ok(new AdminPage<AdminAuditEntry>(items, total, page, pageSize));
    }
}
