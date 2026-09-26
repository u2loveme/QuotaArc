using System.Windows;
using QuotaArc.Desktop;
using Xunit;

namespace QuotaArc.Desktop.Tests;

public sealed class WindowPositionStoreTests
{
    [Fact]
    public void PositionValidationRejectsOffscreenSentinelAndAcceptsVisibleWindow()
    {
        Assert.False(WindowPositionStore.IsPositionOnVirtualScreen(-5000, -5000));
        Assert.True(WindowPositionStore.IsPositionOnVirtualScreen(460, 120));
    }

    [Fact]
    public void PersistablePositionRequiresNormalWindowState()
    {
        Assert.False(WindowPositionStore.CanPersist(WindowState.Maximized, 0, 0));
        Assert.False(WindowPositionStore.CanPersist(WindowState.Minimized, 156, 156));
        Assert.False(WindowPositionStore.CanPersist(WindowState.Normal, -5000, -5000));
        Assert.True(WindowPositionStore.CanPersist(WindowState.Normal, 156, 156));
    }
}
