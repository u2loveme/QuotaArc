using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;

namespace QuotaArc.Desktop;

internal static class WindowPositionStore
{
    internal sealed record SavedPosition(
        double Left,
        double Top,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Width = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Height = null);

    private static readonly string PositionPath = Path.Combine(
        QuotaArcProfilePaths.LocalAppDataRoot,
        "QuotaArc",
        "wpf-window-positions.json");

    public static bool TryLoad(string key, double minWidth, double minHeight, out Point position)
        => TryLoad(key, minWidth, minHeight, out position, out _);

    public static bool TryLoad(string key, double minWidth, double minHeight, out Point position, out Size size)
    {
        position = default;
        size = Size.Empty;
        try
        {
            if (!File.Exists(PositionPath))
            {
                return false;
            }

            var saved = JsonSerializer.Deserialize<Dictionary<string, SavedPosition>>(File.ReadAllText(PositionPath));
            if (saved is null || !saved.TryGetValue(key, out var value)
                || !double.IsFinite(value.Left) || !double.IsFinite(value.Top)
                || !IntersectsVirtualScreen(value.Left, value.Top, minWidth, minHeight))
            {
                return false;
            }

            position = new Point(value.Left, value.Top);
            if (IsValidSize(value.Width, minWidth) && IsValidSize(value.Height, minHeight))
            {
                size = new Size(
                    Math.Min(value.Width!.Value, SystemParameters.VirtualScreenWidth),
                    Math.Min(value.Height!.Value, SystemParameters.VirtualScreenHeight));
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                   or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public static void Save(string key, double left, double top)
        => Save(key, left, top, null, null);

    public static void Save(string key, double left, double top, double? width, double? height)
    {
        if (!double.IsFinite(left) || !double.IsFinite(top)
            || (width is not null && !double.IsFinite(width.Value))
            || (height is not null && !double.IsFinite(height.Value))
            || !IsPositionOnVirtualScreen(left, top))
        {
            return;
        }

        try
        {
            var positions = File.Exists(PositionPath)
                ? JsonSerializer.Deserialize<Dictionary<string, SavedPosition>>(File.ReadAllText(PositionPath))
                    ?? new Dictionary<string, SavedPosition>(StringComparer.Ordinal)
                : new Dictionary<string, SavedPosition>(StringComparer.Ordinal);
            var previous = positions.GetValueOrDefault(key);
            positions[key] = new SavedPosition(left, top, width ?? previous?.Width, height ?? previous?.Height);
            var directory = Path.GetDirectoryName(PositionPath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = PositionPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(positions));
            File.Move(temporaryPath, PositionPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                   or ArgumentException or NotSupportedException)
        {
            // Window placement is optional; keep the dashboard usable if it cannot be saved.
        }
    }

    private static bool IsValidSize(double? value, double minimum)
        => value is { } size && double.IsFinite(size) && size >= minimum;

    private static bool IntersectsVirtualScreen(double left, double top, double width, double height)
    {
        var screenLeft = SystemParameters.VirtualScreenLeft;
        var screenTop = SystemParameters.VirtualScreenTop;
        var screenRight = screenLeft + SystemParameters.VirtualScreenWidth;
        var screenBottom = screenTop + SystemParameters.VirtualScreenHeight;
        const double visibleWidth = 96;
        const double visibleHeight = 32;
        return left + Math.Min(width, visibleWidth) > screenLeft
            && left < screenRight - visibleWidth
            && top + Math.Min(height, visibleHeight) > screenTop
            && top < screenBottom - visibleHeight;
    }

    internal static bool IsPositionOnVirtualScreen(double left, double top) =>
        double.IsFinite(left) && double.IsFinite(top)
        && IntersectsVirtualScreen(left, top, 96, 32);

    internal static bool CanPersist(WindowState state, double left, double top) =>
        state == WindowState.Normal && IsPositionOnVirtualScreen(left, top);
}
