using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GridPilot.Core;
using GridPilot.UI;
using GridPilot.Windows;

namespace GridPilot.Tests;

internal static partial class Program
{
    private static async Task ArrangementPreviewNativeTests(DisplayMonitor monitor, IReadOnlyList<PreviewWindowEntry> entries)
    {
        static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
        static void Invoke(object owner, string name, params object[] arguments) => owner.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(owner, arguments);
        static WindowThumbnail[] Thumbnails(DependencyObject root)
        {
            var found = new List<WindowThumbnail>();
            void Visit(DependencyObject current) { if (current is WindowThumbnail thumbnail) found.Add(thumbnail); for (int i = 0; i < VisualTreeHelper.GetChildrenCount(current); i++) Visit(VisualTreeHelper.GetChild(current, i)); }
            Visit(root); return found.ToArray();
        }
        var result = new TaskCompletionSource<PlacementResult>(); int undoCalls = 0;
        Theme.Apply("Dark");
        IReadOnlyList<(WindowTarget Target, PixelRect Bounds)>? accepted = null;
        var sheet = new AutoArrangeWindow(monitor, new(), _ => null, (_, _) => throw new InvalidOperationException("Legacy callback must not run with async callback"), entries,
            applyAsync: (_, placements) => { accepted = placements; return result.Task; }, undoAsync: () => { undoCalls++; return Task.FromResult(new PlacementResult(true)); }) { ShowActivated = false, Topmost = true };
        try
        {
            Field<CheckBox>(sheet, "remember").IsChecked = false;
            var before = entries.Select(e => WindowService.VisibleBounds(e.Target.Handle)).ToArray();
            sheet.Show(); await Task.Delay(300); sheet.UpdateLayout();
            Assert(sheet.PlanThumbnailCount == 2 && sheet.SidebarThumbnailCount == 2, "plan and two-column catalog have distinct real DWM registrations");
            Assert(Thumbnails(sheet).All(t => t.Available), "live arrangement surfaces are available from exact fixture windows");
            Assert(!sheet.AllowsTransparency && sheet.Opacity == 1 && (Native.GetWindowLongPtr(new WindowInteropHelper(sheet).Handle, -20) & 0x80000) == 0, "arrangement preserves nonlayered compositor HWND");
            Assert(sheet.WindowStyle == WindowStyle.None && System.Windows.Shell.WindowChrome.GetWindowChrome(sheet) is not null, "Focus-style custom header retains native resize chrome without layered transparency");
            Assert(Field<Image>(sheet, "selectedIcon").Source is not null && Field<TextBlock>(sheet, "searchHint").Text == "Find a window…", "selected app identity and search affordance remain visible");
            Console.WriteLine($"Arrangement backdrop: {(sheet.GlassActive ? "Smoked Glass active" : "policy-driven Matte Focus fallback")}.");
            // Capture ONLY this topmost owned window's bounds, never the whole desktop.
            using (var dpi = new Native.DpiScope())
            {
                Native.GetWindowRect(new WindowInteropHelper(sheet).Handle, out var nativeBounds);
                var snapshot = DesktopSnapshot.Capture(monitor with { Bounds = nativeBounds.Pixels, Work = nativeBounds.Pixels }).Image!;
                var thumbnail = Thumbnails(Field<Canvas>(sheet, "canvas")).First();
                var center = thumbnail.PointToScreen(new Point(thumbnail.ActualWidth / 2, thumbnail.ActualHeight / 2));
                int x = Math.Clamp((int)((center.X - nativeBounds.Left) / nativeBounds.Pixels.Width * snapshot.PixelWidth), 0, snapshot.PixelWidth - 1);
                int y = Math.Clamp((int)((center.Y - nativeBounds.Top) / nativeBounds.Pixels.Height * snapshot.PixelHeight), 0, snapshot.PixelHeight - 1);
                byte[] pixel = new byte[4]; snapshot.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
                Assert(pixel[0] > 200 && pixel[1] > 200 && pixel[2] > 200, "plan thumbnail contains white source-fixture compositor pixels, not placeholder rectangles");
                if (Environment.GetCommandLineArgs().Contains("--inspect-arrangement"))
                {
                    string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"GridPilot-live-arrangement-{Environment.ProcessId}.png");
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(snapshot)); using var file = System.IO.File.Create(path); encoder.Save(file);
                    Console.WriteLine("Owned compositor review image: " + path);
                }
            }
            var original = Field<ArrangementPlan>(sheet, "plan"); var oldThumbnails = Thumbnails(Field<Canvas>(sheet, "canvas"));
            Invoke(sheet, "Reroll");
            Assert(!original.Slots.SequenceEqual(Field<ArrangementPlan>(sheet, "plan").Slots), "reroll previews a geometrically distinct feasible plan");
            Assert(oldThumbnails.All(t => !t.HasRegistration), "reroll disposes every superseded plan thumbnail");
            await Task.Delay(100); sheet.UpdateLayout(); Assert(sheet.PlanThumbnailCount == 2, "reroll creates thumbnails at new plan rectangles");
            var rolled = Field<ArrangementPlan>(sheet, "plan"); Invoke(sheet, "SwapProposal", "1", "2");
            var swapped = Field<ArrangementPlan>(sheet, "plan");
            Assert(swapped.Slots.Single(s => s.Id == "1").Bounds == rolled.Slots.Single(s => s.Id == "2").Bounds, "drag-swap path exchanges exact proposed frame dimensions");
            Assert(entries.Select(e => WindowService.VisibleBounds(e.Target.Handle)).SequenceEqual(before), "reroll and swap never move source windows");
            await Task.Delay(100); sheet.UpdateLayout();
            var search = Field<TextBox>(sheet, "search"); search.Text = "no owned window matches";
            Assert(sheet.SidebarThumbnailCount == 0 && sheet.PlanThumbnailCount == 2, "filter releases catalog thumbnails while keeping the plan live");
            search.Clear(); await Task.Delay(100); sheet.UpdateLayout(); Assert(sheet.SidebarThumbnailCount == 2, "clearing search restores visible catalog registrations");
            sheet.Width += 70; sheet.Height -= 50; await Task.Delay(100); sheet.UpdateLayout();
            Assert(Thumbnails(sheet).All(t => t.HasRegistration), "resize keeps valid plan and sidebar thumbnail positions");
            Field<Button>(sheet, "applyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(accepted is not null && accepted[0].Bounds == swapped.Slots[0].Bounds, "Accept uses the edited proposal instead of recalculating it");
            Assert(!((UIElement)sheet.Content).IsEnabled && sheet.IsVisible, "async placement disables editing and keeps the sheet present while pending");
            result.SetResult(new(true)); await Task.Delay(50);
            Assert(sheet.IsVisible && ((UIElement)sheet.Content).IsEnabled, "successful Accept stays open by default");
            var undo = Field<Button>(sheet, "undoButton"); Assert(undo.IsEnabled, "successful Accept exposes grouped Undo while global shortcuts are suspended");
            undo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(30); Assert(undoCalls == 1 && !undo.IsEnabled, "sheet Undo invokes callback once and consumes successful group");
            Field<CheckBox>(sheet, "closeAfter").IsChecked = true; Field<Button>(sheet, "applyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(30);
            Assert(!sheet.IsVisible && sheet.PlanThumbnailCount == 0 && sheet.SidebarThumbnailCount == 0, "close-after-success releases all DWM resources");
        }
        finally { sheet.Close(); }
        var failure = new AutoArrangeWindow(monitor, new(), _ => null, (_, _) => { }, entries,
            applyAsync: (_, _) => Task.FromResult(new PlacementResult(false, "Owned test refusal")), closeAfterPlacement: true) { ShowActivated = false };
        failure.Show(); Field<CheckBox>(failure, "remember").IsChecked = false;
        Field<Button>(failure, "applyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(30);
        Assert(failure.IsVisible && Field<TextBlock>(failure, "status").Text == "Owned test refusal", "failed async placement stays open even with close-after enabled and shows native reason");
        failure.Close();

        using var manyFixture = StartFixture("--sixteen");
        AutoArrangeWindow? catalog = null;
        try
        {
            await WaitForFixture(manyFixture); PreviewWindowEntry[] many = [];
            for (int attempt = 0; attempt < 40; attempt++) { many = PreviewWindows.Capture().Where(e => e.Target.ProcessId == manyFixture.Id).ToArray(); if (many.Length == 16) break; await Task.Delay(25); }
            Assert(many.Length == 16, "scroll fixture owns sixteen real windows");
            catalog = new AutoArrangeWindow(monitor, new(), _ => null, (_, _) => { }, many) { ShowActivated = false, Topmost = true, Width = 900, Height = 650 };
            catalog.Show(); await Task.Delay(200); catalog.UpdateLayout();
            var scroll = Field<ScrollViewer>(catalog, "sidebar"); var all = Thumbnails(scroll);
            Assert(catalog.SidebarThumbnailCount is > 0 and < 16 && scroll.ScrollableHeight > 0, "two-column sidebar registers only visible rows in a bounded viewport");
            var top = all.First(t => t.HasRegistration); scroll.ScrollToEnd(); await Task.Delay(200); catalog.UpdateLayout();
            Assert(!top.HasRegistration && all.Last().HasRegistration, "scroll releases offscreen registrations and creates newly visible sources");
            var presenter = (ScrollContentPresenter)scroll.Template.FindName("PART_ScrollContentPresenter", scroll)!;
            var viewport = presenter.TransformToAncestor(catalog).TransformBounds(new Rect(presenter.RenderSize)); var dpi = VisualTreeHelper.GetDpi(catalog);
            foreach (var thumbnail in all.Where(t => t.HasRegistration))
            {
                object properties = typeof(WindowThumbnail).GetField("last", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(thumbnail)!;
                var destination = (Native.Rect)properties.GetType().GetField("Destination")!.GetValue(properties)!;
                Assert(destination.Top >= Math.Floor(viewport.Top * dpi.DpiScaleY) && destination.Bottom <= Math.Ceiling(viewport.Bottom * dpi.DpiScaleY), "native DWM destination is clipped to sidebar viewport, not painted over inspector/footer");
            }
            manyFixture.Kill(); await manyFixture.WaitForExitAsync(); await Task.Delay(300);
            Assert(catalog.SidebarThumbnailCount == 0 && catalog.PlanThumbnailCount == 0 && !Field<Button>(catalog, "applyButton").IsEnabled, "stale source identities cancel acceptance and release all native previews");
        }
        finally { catalog?.Close(); if (!manyFixture.HasExited) { manyFixture.Kill(); await manyFixture.WaitForExitAsync(); } }
    }
}
