namespace EeveeCore.AuthHandlers;

/// <summary>
///     Shared constants for the dashboard user-identity JWT scheme. Tokens are
///     minted by the PokeFlux dashboard (signed with the shared
///     <c>JwtSecret</c> credential) and verified by the bot, giving
///     controllers a trustworthy Fluxer user identity instead of the anonymous
///     shared-API-key principal.
/// </summary>
public static class DashJwtConstants
{
    /// <summary>
    ///     Authentication scheme name for the dashboard user JWT.
    /// </summary>
    public const string SchemeName = "DashJwt";

    /// <summary>
    ///     Expected token issuer. Must match the value the dashboard mints with.
    /// </summary>
    public const string Issuer = "pokeflux-dashboard";

    /// <summary>
    ///     Expected token audience. Must match the value the dashboard mints with.
    /// </summary>
    public const string Audience = "pokeflux-botapi";

    /// <summary>
    ///     Expected value of the <see cref="ScopeClaim" /> claim for tokens that
    ///     are allowed to reach the bot API.
    /// </summary>
    public const string BackendScope = "botapi";

    /// <summary>
    ///     Claim carrying the Fluxer user id of the dashboard user.
    /// </summary>
    public const string UserIdClaim = "sub";

    /// <summary>
    ///     Claim carrying the Fluxer username of the dashboard user.
    /// </summary>
    public const string UserNameClaim = "name";

    /// <summary>
    ///     Claim carrying the unique token id.
    /// </summary>
    public const string TokenIdClaim = "jti";

    /// <summary>
    ///     Claim carrying the token scope. Only <see cref="BackendScope" /> tokens
    ///     are accepted by the bot API.
    /// </summary>
    public const string ScopeClaim = "scope";
}
