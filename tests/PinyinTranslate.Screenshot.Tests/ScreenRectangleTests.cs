using System.Windows;
using PinyinTranslate.App;

namespace PinyinTranslate.Screenshot.Tests;

public sealed class ScreenRectangleTests
{
    private static readonly ScreenRectangle VirtualScreen = new(-1920, -300, 4480, 1740);

    [Fact]
    public void NegativeMonitorCoordinatesAndFractionalPixelsArePreserved()
    {
        var actual = ScreenRectangle.FromDrag(new(-200.1, 5.6), new(-100.4, 51.2), VirtualScreen);
        Assert.Equal(new ScreenRectangle(-201, 5, 101, 47), actual);
    }

    [Fact]
    public void ReversingDragDirectionProducesSameRectangle()
    {
        var a = new Point(-640.3, -100.2);
        var b = new Point(100.5, 250.9);
        var forward = ScreenRectangle.FromDrag(a, b, VirtualScreen);
        Assert.Equal(new ScreenRectangle(-641, -101, 742, 352), forward);
        Assert.Equal(forward, ScreenRectangle.FromDrag(b, a, VirtualScreen));
    }

    [Fact]
    public void DragIsClippedToPhysicalVirtualScreenBounds()
    {
        var actual = ScreenRectangle.FromDrag(new(-3000, -400), new(3000, 2000), VirtualScreen);
        Assert.Equal(VirtualScreen, actual);
    }

    [Fact]
    public void PartiallyOutsideDragIsClippedOnEachAffectedEdge()
    {
        var actual = ScreenRectangle.FromDrag(new(-2000, -350), new(-1000, 100), VirtualScreen);
        Assert.Equal(new ScreenRectangle(-1920, -300, 920, 400), actual);
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(0, 0, 1, 1)]
    [InlineData(0, 0, 1, 20)]
    [InlineData(0, 0, 20, 1)]
    [InlineData(-4000, -600, -3000, -400)]
    public void TinyOrCompletelyOutsideDragHasNoSelection(double x1, double y1, double x2, double y2)
    {
        Assert.Null(ScreenRectangle.FromDrag(new(x1, y1), new(x2, y2), VirtualScreen));
    }

    [Theory]
    [InlineData(double.NaN, 0, 100, 100)]
    [InlineData(0, double.PositiveInfinity, 100, 100)]
    [InlineData(0, 0, double.NegativeInfinity, 100)]
    [InlineData(0, 0, 100, double.NaN)]
    public void NonFiniteCoordinatesAreRejected(double x1, double y1, double x2, double y2)
    {
        Assert.Null(ScreenRectangle.FromDrag(new(x1, y1), new(x2, y2), VirtualScreen));
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(-1, 100)]
    public void InvalidScreenBoundsAreRejected(int width, int height)
    {
        Assert.Null(ScreenRectangle.FromDrag(new(0, 0), new(50, 50), new(0, 0, width, height)));
    }
}
