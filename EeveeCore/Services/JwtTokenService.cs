using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using EeveeCore.Common.AuthModels;
using Microsoft.IdentityModel.Tokens;
using LinqToDB;
using EeveeCore.Database.Linq.Models.Bot;
using LinqToDB.Async;
using User = EeveeCore.Database.Linq.Models.Bot.User;

namespace EeveeCore.Services;

/// <summary>
///     Service for handling JWT token generation and Discord OAuth integration.
///     Provides stateless JWT authentication without refresh token functionality.
/// </summary>
public class JwtTokenService : INService
{
    private readonly LinqToDbConnectionProvider _dbProvider;
    private readonly BotCredentials _credentials;
    private readonly HttpClient _httpClient;
    private readonly ILogger<JwtTokenService> _logger;

    /// <summary>
    ///     Initializes a new instance of the <see cref="JwtTokenService"/> class.
    /// </summary>
    /// <param name="dbProvider">The LinqToDB connection provider.</param>
    /// <param name="credentials">The bot credentials containing JWT and Discord secrets.</param>
    /// <param name="httpClient">The HTTP client for Discord API calls.</param>
    /// <param name="logger">The logger instance.</param>
    public JwtTokenService(LinqToDbConnectionProvider dbProvider, BotCredentials credentials, 
        HttpClient httpClient, ILogger<JwtTokenService> logger)
    {
        _dbProvider = dbProvider;
        _credentials = credentials;
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    ///     Authenticates a user with Discord OAuth code and generates a JWT token.
    /// </summary>
    /// <param name="discordCode">The Discord OAuth authorization code.</param>
    /// <param name="redirectUri">The redirect URI used in the OAuth flow.</param>
    /// <returns>A JWT token response containing access token and user information.</returns>
    public async Task<JwtTokenResponse?> AuthenticateWithDiscordAsync(string discordCode, string? redirectUri = null)
    {
        try
        {
            // Exchange Discord code for access token
            var discordToken = await GetDiscordTokenAsync(discordCode, redirectUri);
            if (discordToken == null)
            {
                _logger.LogWarning("Failed to get Discord token");
                return null;
            }

            // Get Discord user info
            var discordUser = await GetDiscordUserInfoAsync(discordToken.Access_Token);
            if (discordUser == null)
            {
                _logger.LogWarning("Failed to get Discord user info");
                return null;
            }

            // Get user from database
            await using var db = await _dbProvider.GetConnectionAsync();
            var user = await db.Users.FirstOrDefaultAsync(u => u.UserId == ulong.Parse(discordUser.Id));
            
            if (user == null)
            {
                _logger.LogInformation("User {UserId} not found in database - they must /start first", discordUser.Id);
                return null;
            }

            // Check if user is banned
            if (user.BotBanned == true)
            {
                _logger.LogWarning("Banned user {UserId} attempted to authenticate", discordUser.Id);
                return null;
            }

            // Store/update Discord token
            await StoreDiscordTokenAsync(discordUser, discordToken);

            // Generate JWT token (24 hour expiry)
            var accessToken = GenerateAccessToken(user, discordUser);
            var expiresAt = DateTime.UtcNow.AddHours(24);

            return new JwtTokenResponse
            {
                AccessToken = accessToken,
                RefreshToken = "", // Not used in stateless JWT
                ExpiresAt = expiresAt,
                User = new UserProfileDto
                {
                    UserId = user.UserId.GetValueOrDefault().ToString(),
                    Username = discordUser.Username,
                    Avatar = discordUser.Avatar,
                    Discriminator = discordUser.Discriminator,
                    IsAdmin = IsAdmin(user),
                    Staff = user.Staff ?? "User",
                    IsBotOwner = _credentials.OwnerIds.Contains(user.UserId.GetValueOrDefault())
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error authenticating with Discord");
            return null;
        }
    }

    /// <summary>
    ///     Exchanges a Discord OAuth code for an access token.
    /// </summary>
    /// <param name="code">The Discord OAuth authorization code.</param>
    /// <param name="redirectUri">The redirect URI used in the OAuth flow.</param>
    /// <returns>The Discord token response, or null if the exchange failed.</returns>
    private async Task<DiscordTokenResponse?> GetDiscordTokenAsync(string code, string? redirectUri)
    {
        var parameters = new Dictionary<string, string>
        {
            {"client_id", _credentials.DiscordClientId},
            {"client_secret", _credentials.DiscordClientSecret},
            {"grant_type", "authorization_code"},
            {"code", code}
        };

        if (!string.IsNullOrEmpty(redirectUri))
        {
            parameters.Add("redirect_uri", redirectUri);
        }

        var content = new FormUrlEncodedContent(parameters);
        var response = await _httpClient.PostAsync("https://discord.com/api/oauth2/token", content);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Discord token exchange failed: {StatusCode}", response.StatusCode);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<DiscordTokenResponse>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        });
    }

    /// <summary>
    ///     Gets Discord user information using an access token.
    /// </summary>
    /// <param name="accessToken">The Discord access token.</param>
    /// <returns>The Discord user information, or null if the request failed.</returns>
    private async Task<DiscordUserInfo?> GetDiscordUserInfoAsync(string accessToken)
    {
        _httpClient.DefaultRequestHeaders.Authorization = 
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        var response = await _httpClient.GetAsync("https://discord.com/api/users/@me");
        
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Discord user info request failed: {StatusCode}", response.StatusCode);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<DiscordUserInfo>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    /// <summary>
    ///     Stores or updates a Discord token in the database.
    /// </summary>
    /// <param name="discordUser">The Discord user information.</param>
    /// <param name="discordToken">The Discord token response.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task StoreDiscordTokenAsync(DiscordUserInfo discordUser, DiscordTokenResponse discordToken)
    {
        await using var db = await _dbProvider.GetConnectionAsync();
        
        var existingToken = await db.Tokens.FirstOrDefaultAsync(t => t.UserId == discordUser.Id);
        
        if (existingToken != null)
        {
            await db.Tokens.Where(t => t.UserId == discordUser.Id)
                .Set(t => t.Username, discordUser.Username)
                .Set(t => t.Avatar, discordUser.Avatar)
                .Set(t => t.Discriminator, discordUser.Discriminator)
                .Set(t => t.RefreshToken, discordToken.Refresh_Token)
                .UpdateAsync();
        }
        else
        {
            var newToken = new Token
            {
                TokenValue = Guid.NewGuid().ToString(),
                UserId = discordUser.Id,
                Username = discordUser.Username,
                Avatar = discordUser.Avatar,
                Discriminator = discordUser.Discriminator,
                RefreshToken = discordToken.Refresh_Token
            };
            
            await db.InsertAsync(newToken);
        }

    }

    /// <summary>
    ///     Generates a JWT access token for a user.
    /// </summary>
    /// <param name="user">The user from the database.</param>
    /// <param name="discordUser">The Discord user information.</param>
    /// <returns>The generated JWT access token.</returns>
    public string GenerateAccessToken(User user, DiscordUserInfo discordUser)
    {
        var claims = new[]
        {
            new Claim("UserId", user.UserId.GetValueOrDefault().ToString()),
            new Claim("Username", discordUser.Username ?? ""),
            new Claim("Staff", user.Staff ?? "User"),
            new Claim("IsBotOwner", _credentials.OwnerIds.Contains(user.UserId.GetValueOrDefault()).ToString()),
            new Claim("IsAdmin", IsAdmin(user).ToString()),
            new Claim(JwtRegisteredClaimNames.Iat, 
                new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds().ToString(), 
                ClaimValueTypes.Integer64)
        };

        var key = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(_credentials.JwtSecret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            expires: DateTime.UtcNow.AddHours(24),
            claims: claims,
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    ///     Determines if a user has admin privileges.
    /// </summary>
    /// <param name="user">The user to check.</param>
    /// <returns>True if the user is an admin, false otherwise.</returns>
    private bool IsAdmin(User user)
    {
        return _credentials.OwnerIds.Contains(user.UserId.GetValueOrDefault()) || 
               (!string.IsNullOrEmpty(user.Staff) && 
                user.Staff.ToLower() != "user");
    }
}