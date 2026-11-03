using LinqToDB.Mapping;

namespace EeveeCore.Database.Linq.Models.Bot;

/// <summary>
///     A staff action taken through the dashboard.
/// </summary>
[Table("staff_audit_log")]
public class StaffAuditEntry
{
    /// <summary>
    ///     Gets or sets the entry ID.
    /// </summary>
    [PrimaryKey]
    [Identity]
    [Column("id")]
    public long Id { get; set; }

    /// <summary>
    ///     Gets or sets the staff member who took the action.
    /// </summary>
    [Column("staff_id")]
    [NotNull]
    public ulong StaffId { get; set; }

    /// <summary>
    ///     Gets or sets the staff member's name when they took the action.
    /// </summary>
    [Column("staff_name")]
    public string? StaffName { get; set; }

    /// <summary>
    ///     Gets or sets the action, such as <c>tradeban</c>.
    /// </summary>
    [Column("action")]
    [NotNull]
    public string Action { get; set; } = null!;

    /// <summary>
    ///     Gets or sets the user the action was taken on.
    /// </summary>
    [Column("target_user_id")]
    public ulong? TargetUserId { get; set; }

    /// <summary>
    ///     Gets or sets more about the action, such as the listing or the other user.
    /// </summary>
    [Column("details")]
    public string? Details { get; set; }

    /// <summary>
    ///     Gets or sets the reason the staff member gave.
    /// </summary>
    [Column("reason")]
    [NotNull]
    public string Reason { get; set; } = null!;

    /// <summary>
    ///     Gets or sets when the action was taken.
    /// </summary>
    [Column("created_at")]
    [NotNull]
    public DateTime CreatedAt { get; set; }
}
