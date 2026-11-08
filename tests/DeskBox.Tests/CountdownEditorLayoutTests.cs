using DeskBox.Controls.WidgetContents;
using Windows.Graphics;

namespace DeskBox.Tests;

public sealed class CountdownEditorLayoutTests
{
    [Theory]
    [InlineData(1, 480, 620)]
    [InlineData(1.5, 720, 930)]
    [InlineData(2, 960, 1240)]
    public void EditorHasItsOwnReadableSizeRegardlessOfWidgetSize(double scale, int width, int height)
    {
        var bounds = CountdownEditorLayout.GetBounds(new RectInt32(0, 0, 2560, 1440), scale);
        Assert.Equal(width, bounds.Width);
        Assert.Equal(height, bounds.Height);
        Assert.Equal((2560 - width) / 2, bounds.X);
        Assert.Equal((1440 - height) / 2, bounds.Y);
    }

    [Fact]
    public void HighDpiEditorStaysInsideTheAvailableScreen()
    {
        var area = new RectInt32(-800, 40, 800, 600);
        var bounds = CountdownEditorLayout.GetBounds(area, 2);
        Assert.InRange(bounds.X, area.X, area.X + area.Width - bounds.Width);
        Assert.InRange(bounds.Y, area.Y, area.Y + area.Height - bounds.Height);
        Assert.InRange(bounds.Width, 1, area.Width - 32);
        Assert.InRange(bounds.Height, 1, area.Height - 32);
    }
}
