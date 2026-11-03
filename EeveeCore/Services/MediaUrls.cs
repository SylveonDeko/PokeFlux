using System.Security.Cryptography;
using System.Text;

namespace EeveeCore.Services;

/// <summary>
///     Gets the hosted URLs of images in <c>data/</c>, named by a keyed hash of their path.
/// </summary>
/// <remarks>
///     Files are uploaded by <c>tools/upload-cdn.sh</c> with the same naming.
/// </remarks>
public sealed class MediaUrls(BotCredentials credentials) : INService
{
    private static readonly HashSet<string> HostedExtensions = [".png", ".gif", ".jpg", ".jpeg", ".webp"];

    /// <summary>
    ///     Gets the hosted URL of an image.
    /// </summary>
    /// <param name="path">The image's path, relative to the bot's folder, such as <c>data/images/1-0-.png</c>.</param>
    /// <returns>The URL, or null when hosting is not set up, the file does not exist or is not an image.</returns>
    public string? Get(string? path)
    {
        if (credentials.CdnBaseUrl is not { Length: > 0 } baseUrl || credentials.CdnKey is not { Length: > 0 } key
                                                                  || string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;

        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (!HostedExtensions.Contains(extension))
            return null;

        return $"{baseUrl}/{HostedName(key, path)}{extension}";
    }

    /// <summary>
    ///     Gets the hosted file name of an image, without its extension.
    /// </summary>
    /// <param name="key">The naming secret.</param>
    /// <param name="path">The image's path, relative to the bot's folder.</param>
    public static string HostedName(string key, string path)
    {
        var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), Path.GetFullPath(path)).Replace('\\', '/');
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(relative));
        return Convert.ToHexStringLower(hash)[..32];
    }
}
