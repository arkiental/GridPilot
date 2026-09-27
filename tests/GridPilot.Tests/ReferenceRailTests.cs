using GridPilot.Core;
using System.Windows;
using GridPilot.UI;
using GridPilot.Windows;
namespace GridPilot.Tests;
internal static partial class Program
{
    private static void ReferenceCropTests()
    {
        Assert(ReferenceCrop.Full.Pixels(200, 100) == new PixelRect(0, 0, 200, 100), "full DWM surface crop includes origin");
        var crop = new ReferenceCrop(.25, .2, .5, .4);
        Assert(crop.Pixels(200, 100) == new PixelRect(50, 20, 150, 61), "normalized crop conservatively rounds physical edges");
        Assert(crop.Pixels(400, 200).Left == 100, "source resize scales region independently of destination DPI");
        Assert(!new ReferenceCrop(0, 0, 0, 1).Valid && !new ReferenceCrop(double.NaN, 0, 1, 1).Valid && !new ReferenceCrop(.9, 0, .2, 1).Valid, "invalid crops rejected");
        Assert(!crop.Pixels(0, 100).IsValid, "zero source has no region");
        var fit = ReferenceCrop.Contain(200, 100, new Rect(0, 0, 100, 100));
        Assert(fit == new Rect(0, 25, 100, 50), "editor contains without distortion");
        Assert(ReferenceCrop.FromPoints(new Point(25, 0), new Point(75, 100), fit) == new ReferenceCrop(.25, 0, .5, 1), "pointer crop clamps letterboxing");
        Assert(!ReferenceCrop.FromPoints(new Point(), new Point(), Rect.Empty).Valid, "empty preview has no crop");
        var mapping = WindowThumbnail.FitCrop(new ReferenceCrop(.25, .25, .5, .5), 400, 200, new Rect(-100, 0, 200, 200), new Rect(-100, 0, 200, 200));
        Assert(mapping is { } m && m.Source == new PixelRect(100, 50, 300, 150) && m.Destination == new PixelRect(-100, 50, 100, 150), "explicit source offsets and negative destination contain correctly");
    }
    private static async Task ReferenceRailNativeTests()
    {
        using var fixture = StartFixture("--sixteen"); ReferenceRailWindow? rail = null;
        try
        {
            await WaitForFixture(fixture); await Task.Delay(200);
            var entry = PreviewWindows.Capture().First(e => e.Target.ProcessId == fixture.Id);
            Theme.Apply("Dark"); rail = new ReferenceRailWindow(); rail.Show();
            for (int i = 0; i < 5; i++) rail.Add(entry.Target, new ReferenceCrop(i % 2 * .5, 0, .5, 1));
            rail.UpdateLayout(); await Task.Delay(600);
            Assert(rail.CardCount == 4, "reference rail rejects fifth crop");
            Assert(rail.RegistrationCount > 0, "reference rail registers visible cropped previews");
            for (int i = 0; i < 5; i++)
            {
                rail.Hide(); Assert(rail.RegistrationCount == 0, "hiding rail releases all DWM registrations");
                rail.Show(); rail.UpdateLayout(); await Task.Delay(550); Assert(rail.RegistrationCount > 0, "reopen recreates valid previews");
            }
            Native.ShowWindowAsync(entry.Target.Handle, 6); await Task.Delay(650);
            Assert(rail.RegistrationCount == 0, "minimized source releases every crop");
            Native.ShowWindowAsync(entry.Target.Handle, 9); await Task.Delay(650);
            Assert(rail.RegistrationCount > 0, "restored source re-registers crops");
            fixture.Kill(); await fixture.WaitForExitAsync(); await Task.Delay(650);
            Assert(rail.RegistrationCount == 0 && rail.CardCount == 4, "closed source retains removable placeholders without registrations");
            rail.Shutdown(); Assert(rail.RegistrationCount == 0, "shutdown releases thumbnails"); rail = null;
        }
        finally { rail?.Shutdown(); if (!fixture.HasExited) { fixture.Kill(); await fixture.WaitForExitAsync(); } }
    }
}

