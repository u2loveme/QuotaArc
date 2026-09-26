using QuotaArc.Desktop;
using Xunit;

namespace QuotaArc.Desktop.Tests;

public sealed class MaximizedWorkAreaTests
{
    [Theory]
    [InlineData(0, 0, 0, 0, 1920, 1040, 0, 0, 1920, 1040)]
    [InlineData(-1920, 0, -1920, 0, 0, 1040, 0, 0, 1920, 1040)]
    [InlineData(0, 0, 40, 0, 1920, 1040, 40, 0, 1880, 1040)]
    [InlineData(0, 0, 0, 48, 1920, 1080, 0, 48, 1920, 1032)]
    public void MaximizedBoundsRespectMonitorWorkArea(
        int monitorLeft,
        int monitorTop,
        int workLeft,
        int workTop,
        int workRight,
        int workBottom,
        int expectedX,
        int expectedY,
        int expectedWidth,
        int expectedHeight)
    {
        var bounds = MainWindow.CalculateMaximizedWorkAreaBounds(
            monitorLeft, monitorTop, workLeft, workTop, workRight, workBottom);

        Assert.Equal(new MaximizedWorkAreaBounds(expectedX, expectedY, expectedWidth, expectedHeight), bounds);
    }
}
