using GridPilot.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
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
    private static T[] RailDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var found = new List<T>();
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); if (child is T value) found.Add(value); found.AddRange(RailDescendants<T>(child));
        }
        return found.ToArray();
    }
    private static System.Drawing.Color OwnedPreviewPixel(FrameworkElement preview)
    {
        var point = preview.PointToScreen(new Point(preview.ActualWidth / 2, preview.ActualHeight / 2));
        using var bitmap = new System.Drawing.Bitmap(1, 1);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.CopyFromScreen((int)point.X, (int)point.Y, 0, 0, new System.Drawing.Size(1, 1));
        return bitmap.GetPixel(0, 0);
    }
    private static async Task ReferencePixelsNativeTests()
    {
        var grid = new Grid(); grid.RowDefinitions.Add(new()); grid.RowDefinitions.Add(new()); grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new());
        var red = new Border { Background = Brushes.Red }; grid.Children.Add(red);
        var green = new Border { Background = Brushes.Lime }; Grid.SetColumn(green, 1); grid.Children.Add(green);
        var blue = new Border { Background = Brushes.Blue }; Grid.SetRow(blue, 1); grid.Children.Add(blue);
        var yellow = new Border { Background = Brushes.Yellow }; Grid.SetRow(yellow, 1); Grid.SetColumn(yellow, 1); grid.Children.Add(yellow);
        var source = new Window { Title = "Owned reference quadrants", Width = 400, Height = 400, WindowStyle = WindowStyle.None, Content = grid, ShowActivated = false };
        var rail = new ReferenceRailWindow(); Window? cover = null; ReferenceCropEditor? editor = null;
        try
        {
            Theme.Apply("Dark"); source.Show(); await Task.Delay(150);
            var target = new WindowTarget(new WindowInteropHelper(source).Handle, (uint)Environment.ProcessId, source.Title);
            var crop = new ReferenceCrop(.05, .05, .35, .35); rail.Add(target, crop); rail.Show(); rail.UpdateLayout(); await Task.Delay(650);
            var pixel = OwnedPreviewPixel(rail.PreviewAt(0)); Assert(pixel.R > 200 && pixel.G < 40 && pixel.B < 40, "explicit crop displays red quadrant, not whole source center");
            cover = new Window { Width = source.ActualWidth, Height = source.ActualHeight, Left = source.Left, Top = source.Top, WindowStyle = WindowStyle.None, Background = Brushes.Magenta, ShowActivated = false }; cover.Show();
            red.Background = Brushes.Cyan; await Task.Delay(650);
            pixel = OwnedPreviewPixel(rail.PreviewAt(0)); Assert(pixel.R < 40 && pixel.G > 200 && pixel.B > 200, "covered source crop reflects changed compositor pixels");
            source.Width = 500; source.Height = 450; rail.Width = 400; await Task.Delay(650);
            Assert(rail.PreviewAt(0).Crop == crop, "rail resize preserves selected normalized region");
            pixel = OwnedPreviewPixel(rail.PreviewAt(0)); Assert(pixel.G > 200 && pixel.B > 200, "source resize maps selected region to same quadrant");
            for (int i = 1; i < 4; i++) rail.Add(target, crop);
            await Task.Delay(1000);
            var process = System.Diagnostics.Process.GetCurrentProcess(); var cpu = process.TotalProcessorTime; int handles = process.HandleCount; var clock = System.Diagnostics.Stopwatch.StartNew(); await Task.Delay(1500); process.Refresh();
            Console.WriteLine($"Reference four-card session: {(process.TotalProcessorTime - cpu).TotalMilliseconds:F1} ms process CPU / {clock.ElapsedMilliseconds} ms wall; {rail.RegistrationCount} visible registrations; handles {handles}->{process.HandleCount}.");
            rail.Hide(); Assert(rail.RegistrationCount == 0 && !rail.RefreshRunning, "hidden four-card session has no native registrations or timer");
            cover.Close(); cover = null;
            editor = new ReferenceCropEditor(target, ReferenceCrop.Full); editor.Show(); editor.UpdateLayout(); await Task.Delay(650);
            var fields = RailDescendants<TextBox>(editor); Assert(fields.Length == 4, "crop editor exposes four keyboard numeric inputs");
            string[] values = ["0", "0", "100", "100"]; for (int i = 0; i < 4; i++) fields[i].Text = values[i];
            var pin = RailDescendants<Button>(editor).Single(b => Equals(b.Content, "Pin crop")); Assert(pin.IsEnabled && editor.Result.Pixels(RailDescendants<WindowThumbnail>(editor).Single(t => t.Crop == ReferenceCrop.Full).SourceSize.Width, RailDescendants<WindowThumbnail>(editor).Single(t => t.Crop == ReferenceCrop.Full).SourceSize.Height).Width == 100, "numeric fields set valid source-pixel crop");
            fields[2].Text = "0"; await Task.Delay(650); Assert(!pin.IsEnabled, "periodic refresh cannot re-enable invalid numeric crop");
            fields[2].Text = "100"; Assert(pin.IsEnabled, "valid numeric crop restores pin action");
            var overlayField = typeof(ReferenceCropEditor).GetField("overlay", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var overlay = (Canvas)overlayField.GetValue(editor)!;
            var beforeCapture = editor.Result;
            overlay.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = System.Windows.Input.Mouse.MouseDownEvent });
            overlay.ReleaseMouseCapture();
            Assert(editor.Result == beforeCapture && typeof(ReferenceCropEditor).GetField("start", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(editor) is null, "lost capture cancels crop gesture and restores baseline");
            // Nonmodal fixture closes directly; modal production dialog commits only on Pin crop.
            editor.Close(); editor = null; Assert(rail.PreviewAt(0).Crop == crop, "cancelling editor leaves existing card unchanged");
        }
        finally { editor?.Close(); cover?.Close(); rail.Shutdown(); source.Close(); }
    }
    private static async Task ReferenceRailNativeTests()
    {
        await ReferencePixelsNativeTests();
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
                rail.Hide(); Assert(!rail.RefreshRunning, "hidden rail stops lifecycle polling"); Assert(rail.RegistrationCount == 0, "hiding rail releases all DWM registrations");
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





