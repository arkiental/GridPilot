using System.Windows.Controls;
using GridPilot.Core;
using GridPilot.UI;
using GridPilot.Windows;

namespace GridPilot.Tests;

internal static partial class Program
{
    private static async Task PanelPlacementControllerTests()
    {
        bool busy = false; int calls = 0;
        var controller = new PanelPlacementController(() => null, () => false, () => false, () => busy);
        var completion = new TaskCompletionSource<PlacementResult>();
        var first = controller.RunAsync(null, null, () => { calls++; return completion.Task; });
        var rejected = await controller.RunAsync(null, null, () => { calls++; return Task.FromResult(new PlacementResult(true)); });
        Assert(!rejected.Success && calls == 1 && !first.IsCompleted, "second callback cannot displace a pending operation");
        completion.SetResult(new(true)); Assert((await first).Success, "first callback keeps its own completion");
        busy = true;
        Assert(!(await controller.RunAsync(null, null, () => { calls++; return Task.FromResult(new PlacementResult(true)); })).Success && calls == 1, "native busy prevents dispatch");
        busy = false;
        Assert(!(await controller.RunAsync(null, null, () => Task.FromException<PlacementResult>(new InvalidOperationException("fixture refusal")))).Success, "expected async callback fault becomes failure");
        bool propagated = false;
        try { await controller.RunAsync(null, null, () => Task.FromException<PlacementResult>(new NullReferenceException("fixture programming fault"))); }
        catch (NullReferenceException) { propagated = true; }
        Assert(propagated && (await controller.RunAsync(null, null, () => Task.FromResult(new PlacementResult(true)))).Success, "unexpected fault propagates and releases operation ownership");
        Region? savedPlacementSeed = null;
        var requestedRegion = new Region(0, 0, .5, .5);
        completion = new();
        var batch = controller.RunBatchAsync(null, requestedRegion, () => completion.Task, region => savedPlacementSeed = region);
        Assert(savedPlacementSeed is null, "batch does not remember requested rectangle before success");
        requestedRegion = Region.Full;
        completion.SetResult(new(true)); await batch;
        Assert(savedPlacementSeed == new Region(0, 0, .5, .5), "successful batch callback remembers dispatch-time grid rectangle for Add placement");
        await controller.RunBatchAsync(null, Region.Full, () => Task.FromResult(new PlacementResult(false)), region => savedPlacementSeed = region);
        Assert(savedPlacementSeed == new Region(0, 0, .5, .5), "failed batch callback preserves last successful placement seed");
        await controller.RunBatchAsync(null, null, () => Task.FromResult(new PlacementResult(true)), region => savedPlacementSeed = region);
        Assert(savedPlacementSeed == new Region(0, 0, .5, .5), "successful exact swap does not replace proportional placement seed");
    }

    private static async Task PanelPlacementControllerNativeTests()
    {
        using var fixture = StartFixture();
        GridPanel? current = null;
        try
        {
            var hwnd = await WaitForFixture(fixture);
            var target = WindowService.Capture(hwnd, out _)!;
            Theme.Apply("Dark");
            var panel = new GridPanel(target, new AppSettings(), false, (_, _) => null) { DismissOnDeactivate = false };
            current = panel; panel.Show();
            using var newlyOpened = StartFixture();
            try
            {
                var newHwnd = await WaitForFixture(newlyOpened);
                var newTarget = WindowService.Capture(newHwnd, out _)!;
                int oldCardCount = panel.SidebarResultCount;
                bool close = false;
                var controller = new PanelPlacementController(() => current, () => close, () => true, () => false);
                var completion = new TaskCompletionSource<PlacementResult>();
                var operation = controller.RunAsync(panel, newTarget, () => completion.Task);
                Assert(panel.Target.Handle == newTarget.Handle && panel.Target.ProcessId == newTarget.ProcessId, "new foreground callback updates retained panel target identity");
                var grid = (SelectionGrid)panel.FindName("GridControl");
                Assert(grid.TargetBounds == WindowService.VisibleBounds(newHwnd) && panel.SidebarResultCount > oldCardCount, "new foreground target has current red marker and sidebar card");
                Assert(panel.IsOperationPending && !grid.IsEnabled, "integrated callback disables placement while pending");
                Assert(!(await controller.RunAsync(panel, newTarget, () => Task.FromResult(new PlacementResult(true)))).Success && panel.IsOperationPending, "busy second callback cannot clear pending panel state");
                completion.SetResult(new(true)); await operation;
                bool dispatchedDuringRefresh = false;
                Assert(!(await controller.RunAsync(panel, newTarget, () => { dispatchedDuringRefresh = true; return Task.FromResult(new PlacementResult(true)); })).Success && !dispatchedDuringRefresh,
                    "preview refresh cannot race a second external placement callback");
                await Task.Delay(150);
                Assert(panel.IsVisible && !panel.IsOperationPending && grid.IsEnabled, "successful callback refreshes and retains panel by default");
                Assert(((Button)panel.FindName("UndoButton")).IsEnabled, "callback updates available Undo state");
                await controller.RunAsync(panel, null, () => Task.FromException<PlacementResult>(new InvalidOperationException("fixture failure"))); await Task.Delay(150);
                Assert(panel.IsVisible && !panel.IsOperationPending && grid.IsEnabled, "async callback fault releases retained panel");
                bool faultPropagated = false;
                try { await controller.RunAsync(panel, null, () => Task.FromException<PlacementResult>(new NullReferenceException("fixture fault"))); }
                catch (NullReferenceException) { faultPropagated = true; }
                await Task.Delay(150);
                Assert(faultPropagated && !panel.IsOperationPending && grid.IsEnabled, "unexpected callback fault releases panel before propagating");
                close = true;
                await controller.RunAsync(panel, null, () => Task.FromResult(new PlacementResult(false, "fixture refusal"))); await Task.Delay(150);
                Assert(panel.IsVisible, "close-after-placement never closes on refusal");
                await controller.RunAsync(panel, null, () => Task.FromResult(new PlacementResult(true)));
                Assert(!panel.IsVisible, "close-after-placement closes successful owning panel");

                var old = new GridPanel(target, new(), false, (_, _) => null) { DismissOnDeactivate = false };
                current = old; old.Show();
                completion = new();
                operation = controller.RunAsync(old, target, () => completion.Task);
                old.Dismiss(false);
                var replacement = new GridPanel(newTarget, new(), false, (_, _) => null) { DismissOnDeactivate = false };
                current = replacement; replacement.Show();
                completion.SetResult(new(true)); await operation; await Task.Delay(100);
                Assert(replacement.IsVisible && !replacement.IsOperationPending && !old.IsVisible, "late callback cannot close replacement or reopen closed owner");
                replacement.Dismiss(false); current = null;
            }
            finally { if (!newlyOpened.HasExited) { newlyOpened.Kill(); await newlyOpened.WaitForExitAsync(); } }

            // Production capture reads the real catalog. Compare against the same captured
            // originals, without moving or injecting input into any external application.
            var monitor = MonitorService.ForWindow(hwnd, MonitorService.GetAll());
            var settings = new AppSettings();
            settings.ArrangementPreferences.Add(new() { AppId = Windows.ApplicationIdentity.Read(target)!, GridWidth = 3, GridHeight = 2, MinimumWidth = 1, MinimumHeight = 1 });
            var actual = PartialWorkspacePlacement.Capture(target, monitor.Device, settings);
            var obstacles = actual.Originals.Where(e => e.Target.Handle != target.Handle || e.Target.ProcessId != target.ProcessId).Select(e => e.Bounds).ToArray();
            var expected = PartialWorkspace.Find(monitor.Work, monitor.Dpi, settings.Gap, settings.PartialWorkspace, obstacles, 1, 1,
                PartialWorkspacePlacement.EffectiveRatio(settings.ArrangementPreferences[0], monitor.Work, settings.Columns, settings.Rows));
            Assert(actual.Slot == expected, "production live Capture uses actual occupancy and effective saved grid ratio");
            var owned = PartialWorkspacePlacement.Capture(target, monitor.Device, settings,
                () => PreviewWindows.Capture().Where(e => e.Target.ProcessId == fixture.Id).ToArray());
            Assert(owned.Success && owned.Slot.VacantCount == 8 && owned.Originals.Any(e => e.Target.Handle == target.Handle), "owned live catalog ignores target occupancy and retains identity for stale guard");
            Assert(Math.Abs((double)owned.Slot.Bounds!.Value.Width / owned.Slot.Bounds.Value.Height -
                PartialWorkspacePlacement.EffectiveRatio(settings.ArrangementPreferences[0], monitor.Work, settings.Columns, settings.Rows)) < .01,
                "live capture plan honors saved grid shape inside slot");
            var service = new WindowService(() => PreviewWindows.Capture().Where(e => e.Target.ProcessId == fixture.Id).ToArray());
            var before = WindowService.VisibleBounds(hwnd);
            var stale = owned.Originals.Select(e => (e.Target, e.Bounds with { Left = e.Bounds.Left + 20 })).ToArray();
            Assert(!(await service.ArrangeAsync(monitor, [(target, owned.Slot.Bounds.Value)], stale)).Success && WindowService.VisibleBounds(hwnd).Near(before),
                "captured fill plan rejects stale identity geometry before touching target");
            Assert((await service.ArrangeAsync(monitor, [(target, owned.Slot.Bounds.Value)], owned.Originals)).Success,
                "live captured fill plan applies with original occupancy guard");
            Assert((await service.UndoAsync()).Success && WindowService.VisibleBounds(hwnd).Near(before), "live captured guarded fill retains verified target Undo");
        }
        finally
        {
            current?.Dismiss(false);
            if (!fixture.HasExited) { fixture.Kill(); await fixture.WaitForExitAsync(); }
        }
    }
}
