using Hydra.Tray;
using SkiaSharp;

namespace Tests.Tray;

public class TrayIconImageTests
{
    [Test]
    public void GeneratedIcon_IsAValid32PixelPng()
    {
        using var bitmap = SKBitmap.Decode(TrayIconImage.CreatePng());

        Assert.Multiple(() =>
        {
            Assert.That(bitmap, Is.Not.Null);
            Assert.That(bitmap.Width, Is.EqualTo(32));
            Assert.That(bitmap.Height, Is.EqualTo(32));
        });
    }
}
