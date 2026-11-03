namespace EeveeCore.Common;

/// <summary>
///     Constants for the PokeFlux dashboard API.
/// </summary>
public static class ApiConstants
{
    /// <summary>
    ///     The header to use for the API key.
    /// </summary>
    public const string HeaderName = "X-API-Key";

    /// <summary>
    ///     The authorization policy that requires a valid API key.
    /// </summary>
    public const string ApiKeyPolicy = "ApiKeyPolicy";

    /// <summary>
    ///     The authentication scheme name for the API key.
    /// </summary>
    public const string ApiKeyScheme = "ApiKey";
}
