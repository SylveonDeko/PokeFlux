using System.Security.Cryptography;
using System.Text;

namespace EeveeCore.Services.Impl;

/// <summary>
///     Checks the API key the dashboard sends against the configured <c>ApiKey</c>.
/// </summary>
/// <param name="credentials">The bot credentials containing the API key.</param>
public class ApiKeyValidation(BotCredentials credentials) : IApiKeyValidation
{
    /// <inheritdoc />
    public bool IsValidApiKey(string userApiKey)
    {
        if (string.IsNullOrWhiteSpace(userApiKey) || string.IsNullOrWhiteSpace(credentials.ApiKey))
            return false;

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(userApiKey),
            Encoding.UTF8.GetBytes(credentials.ApiKey));
    }
}
