using System.Reflection;
using QuotaArc.Desktop;
using Xunit;

namespace QuotaArc.Desktop.Tests;

public sealed class QuotaArcBrandingTests
{
    [Fact]
    public void AssemblyPublishesQuotaArcIdentity()
    {
        var assembly = typeof(MainWindow).Assembly;

        Assert.Equal("QuotaArc", assembly.GetName().Name);
        Assert.Equal("QuotaArc", assembly.GetCustomAttribute<AssemblyTitleAttribute>()?.Title);
        Assert.Equal("QuotaArc", assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product);
    }

    [Fact]
    public void SupportLinkUsesCanonicalQuotaArcRoute()
    {
        Assert.Equal(
            "https://vivibureau.pp.ua/quotaarc",
            MainWindow.SupportHubUrl);
    }

    [Fact]
    public void RuntimeProfileUsesCanonicalDirectoryAndCentralizesLegacyDirectory()
    {
        Assert.EndsWith(
            Path.Combine("QuotaArc", "telemetry.sqlite3"),
            SqliteTodayUsageReader.ProductionDatabasePath,
            StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(
            LegacyQuotaArcIdentity.DataDirectoryName,
            LegacyQuotaArcIdentity.DataDirectory,
            StringComparison.OrdinalIgnoreCase);
    }
}
