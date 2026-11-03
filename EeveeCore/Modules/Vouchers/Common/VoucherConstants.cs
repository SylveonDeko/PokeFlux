namespace EeveeCore.Modules.Vouchers.Common;

/// <summary>
///     Contains constants used in the voucher system.
/// </summary>
public static class VoucherConstants
{
    /// <summary>
    ///     Available status options for voucher requests.
    /// </summary>
    public static readonly string[] StatusOptions = 
    {
        "Created",
        "Accepted", 
        "Assigned",
        "In-progress",
        "Completed",
        "Denied"
    };

    /// <summary>
    ///     Status colors for different voucher request states.
    /// </summary>
    public static readonly Dictionary<string, Color> StatusColors = new()
    {
        { "Created", Color.Blue },
        { "Accepted", Color.Green },
        { "Assigned", Color.Orange },
        { "In-progress", Color.Gold },
        { "Completed", Color.Purple },
        { "Denied", Color.Red }
    };

    /// <summary>
    ///     Maximum number of concurrent voucher requests per user.
    /// </summary>
    public const int MaxRequestsPerUser = 3;

    /// <summary>
    ///     Timeout for voucher request form completion in minutes.
    /// </summary>
    public const int FormTimeoutMinutes = 10;
}