using System.IO;

namespace QuotaArc.Desktop;

internal static class QuotaArcProfilePaths
{
    internal static string LocalAppDataRoot => ResolveRoot(
        "LOCALAPPDATA", Environment.SpecialFolder.LocalApplicationData);

    internal static string UserProfileRoot => ResolveRoot(
        "USERPROFILE", Environment.SpecialFolder.UserProfile);

    private static string ResolveRoot(string environmentVariable, Environment.SpecialFolder fallback)
    {
        var configured = Environment.GetEnvironmentVariable(environmentVariable);
        return !string.IsNullOrWhiteSpace(configured) && Path.IsPathFullyQualified(configured)
            ? Path.GetFullPath(configured)
            : Environment.GetFolderPath(fallback);
    }
}
