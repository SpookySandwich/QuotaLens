using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using QuotaLens.Core;

namespace QuotaLens.Services;

/// <summary>
/// Extracts and caches the icon from the executable a provider launch button represents.
/// </summary>
public static class LaunchIconService
{
    private static readonly string CacheRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QuotaLens",
        "LaunchIcons");

    public static string? GetOrCreateIconPath(string providerId, ProviderLaunchTarget target, string? customPath)
    {
        return GetOrCreateIconPath(
            providerId,
            target,
            customPath,
            File.Exists,
            Directory.Exists,
            WriteAssociatedIconPng,
            CacheRoot);
    }

    /// <summary>Extracts an icon directly from a resolved executable such as wt.exe.</summary>
    public static string? GetOrCreateIconPath(string executablePath) =>
        GetOrCreateIconPath(
            executablePath,
            File.Exists,
            WriteAssociatedIconPng,
            CacheRoot);

    internal static string? GetOrCreateIconPath(
        string providerId,
        ProviderLaunchTarget target,
        string? customPath,
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists,
        Func<string, string, bool> writeIconPng,
        string cacheRoot)
    {
        if (!IdeLauncher.TryResolveLaunchPath(
                providerId,
                target,
                customPath,
                out var launchPath,
                fileExists,
                directoryExists))
        {
            return null;
        }

        return GetOrCreateIconPath(launchPath, fileExists, writeIconPng, cacheRoot);
    }

    internal static string? GetOrCreateIconPath(
        string executablePath,
        Func<string, bool> fileExists,
        Func<string, string, bool> writeIconPng,
        string cacheRoot)
    {
        if (!fileExists(executablePath))
            return null;

        var cachePath = BuildCachePath(cacheRoot, executablePath);
        if (File.Exists(cachePath))
            return cachePath;

        try
        {
            Directory.CreateDirectory(cacheRoot);
            return writeIconPng(executablePath, cachePath) && File.Exists(cachePath)
                ? cachePath
                : null;
        }
        catch
        {
            return null;
        }
    }

    internal static string BuildCachePath(string cacheRoot, string executablePath)
    {
        var file = new FileInfo(executablePath);
        var identity = string.Join(
            "|",
            file.FullName.ToUpperInvariant(),
            file.Exists ? file.LastWriteTimeUtc.Ticks : 0,
            file.Exists ? file.Length : 0);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(cacheRoot, $"{hash}.png");
    }

    private static bool WriteAssociatedIconPng(string executablePath, string outputPath)
    {
        using var icon = Icon.ExtractAssociatedIcon(executablePath);
        if (icon == null)
            return false;

        using var bitmap = icon.ToBitmap();
        bitmap.Save(outputPath, ImageFormat.Png);
        return true;
    }
}
