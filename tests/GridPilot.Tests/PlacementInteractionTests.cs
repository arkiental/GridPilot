using GridPilot.Core;
using GridPilot.Windows;

namespace GridPilot.Tests;

internal static partial class Program
{
    private static void PlacementInteractionTests()
    {
        var work = new PixelRect(-1920, 0, 0, 1080);
        ReflowWindow[] windows = [new("selected", new(-1900, 20, -1500, 500)), new("affected", new(-960, 0, 0, 1080)), new("untouched", new(-1900, 550, -1500, 1050))];
        var plan = PlacementInteractions.Reflow(work, "selected", new(.5, 0, 1, .5), windows, 6, 6, 8, 96);
        Assert(plan.Success && plan.Slots.Count == 2, "reflow includes only selected and directly overlapped windows");
        Assert(plan.Slots.All(s => s.Id != "untouched"), "unrelated window is never moved");
        Assert(plan.Slots.All(s => !PlacementInteractions.Overlaps(s.Bounds, windows[2].Bounds)), "reflow avoids unrelated occupied cells");
        Assert(!PlacementInteractions.Overlaps(plan.Slots[0].Bounds, plan.Slots[1].Bounds), "reflow never covers displaced window");
        Assert(!PlacementInteractions.Reflow(work, "selected", Region.Full, windows, 6, 6, 8, 96).Success, "full display refuses destructive reflow when no space remains");
        Assert(!PlacementInteractions.Reflow(work, "selected", new(.5, 0, 1, .5), [windows[0], windows[1] with { Resizable = false }], 6, 6, 8, 96).Success, "unavailable overlapping blocker cannot be silently covered");
        Assert(!PlacementInteractions.Reflow(work, "selected", Region.Full, windows, int.MaxValue, 6, 8, 96).Success, "invalid grid dimensions reject before enumeration");
        var a = new ReflowWindow("a", new(-1900, 20, -1200, 620));
        var b = new ReflowWindow("b", new(-1100, 100, -100, 900));
        var swap = PlacementInteractions.Swap(work, a, b);
        Assert(swap.Success && swap.Slots[0].Bounds == b.Bounds && swap.Slots[1].Bounds == a.Bounds, "unequal swap preserves exact position AND size");
        Assert(!PlacementInteractions.Swap(work, a with { MinimumWidth = 1500 }, b).Success, "swap refuses incompatible minimum without resizing");
        var atMinimum = new PixelRect(-1800, 20, -1480, 260);
        var identity = new WindowTarget(1, 2, "constrained");
        var requested = new PixelRect(6148, 3383, 7672, 4216);
        Assert(WindowService.SizeConstraint(identity, requested, new(6148, 3383, 7748, 4583)) is { MinimumWidth: 1600, MinimumHeight: 1200 }, "Spotify response learns physical dimensions, including an off-work-area frame");
        Assert(WindowService.SizeConstraint(identity, requested, requested with { Bottom = 4583 }) is { MinimumWidth: 0, MinimumHeight: 1200 }, "unconstrained axis is not learned as a minimum");
        Assert(WindowService.SizeConstraint(identity, requested, requested) is null, "exact response does not change constraints");
        Assert(WindowService.SizeConstraint(identity, requested, new(0, 0, 1600, 1200)) is null, "wrong-origin response cannot teach size constraints");
        Assert(WindowService.SizeConstraint(identity, requested, requested with { Right = 7748, Bottom = 4000 }) is null, "mixed smaller/larger response is not treated as a minimum");
        var recoveryProbes = WindowService.RecoveryProbes(atMinimum, work, atMinimum);
        Assert(recoveryProbes.Count is > 0 and <= 2 && recoveryProbes.All(p => p.Width >= atMinimum.Width && p.Height >= atMinimum.Height && !p.Near(atMinimum)), "recovery probes preserve minimum dimensions and require actual native response");
        Assert(recoveryProbes.All(p => p.Left >= work.Left && p.Top >= work.Top && p.Right <= work.Right && p.Bottom <= work.Bottom), "recovery uses available on-screen slack");
        var fullProbes = WindowService.RecoveryProbes(work, work, work);
        Assert(fullProbes.Count == 2 && fullProbes.All(p => p.Width >= work.Width && p.Height >= work.Height && p.Left >= work.Left - 2 && p.Top >= work.Top - 2 && p.Right <= work.Right + 2 && p.Bottom <= work.Bottom + 2), "full-work recovery uses only bounded two-pixel excursions without shrinking");
        Assert(PlacementInteractions.Swap(work, a, b with { Bounds = a.Bounds with { Left = -1600 } }).Success, "intentional swap supports overlapping originals");
        foreach (uint dpi in new uint[] { 96, 144, 192 })
        {
            var large = new PixelRect(-7680, -100, 0, 4220);
            ReflowWindow[] input = [new("a", new(-7600, 0, -6000, 1600)), new("b", new(-5000, 0, -2000, 3000))];
            var actual = PlacementInteractions.Reflow(large, "a", new(.5, 0, 1, .5), input, 12, 12, 8, dpi);
            Assert(actual.Success && actual.Slots.All(s => s.Bounds.Left >= large.Left && s.Bounds.Top >= large.Top && s.Bounds.Right <= large.Right && s.Bounds.Bottom <= large.Bottom), "8K negative-origin reflow preserves bounds at every DPI");
        }
    }
    private static async Task PlacementInteractionNativeTests()
    {
        await PendingPlacementRecoveryTests();
        await MinimumPlacementRecoveryTest();
        await ConstrainedArrangementRetryTest();
        using (var delayed = StartFixture("--slow-batch-resize"))
        {
            try
            {
                var hwnd = await WaitForFixture(delayed); await Task.Delay(100);
                var target = WindowService.Capture(hwnd, out _)!;
                var monitor = MonitorService.ForWindow(hwnd, MonitorService.GetAll());
                var original = WindowService.VisibleBounds(hwnd);
                var destination = new PixelRect(original.Left, original.Top, original.Left + Math.Max(320, original.Width - 180), original.Top + Math.Max(240, original.Height - 100));
                var service = new WindowService(() => PreviewWindows.Capture().Where(e => e.Target.ProcessId == delayed.Id).ToArray());
                var result = await service.ArrangeAsync(monitor, [(target, destination)], [(target, original)]);
                Assert(result.Success && WindowService.VisibleBounds(hwnd).Near(destination), "batch waits for one-second async resize instead of rolling a valid placement back: " + result.Message);
                Assert((await service.UndoAsync()).Success && WindowService.VisibleBounds(hwnd).Near(original), "slow batch retains verified Undo");
            }
            finally { if (!delayed.HasExited) { delayed.Kill(); await delayed.WaitForExitAsync(); } }
        }
        using var fixture = StartFixture("--pair");
        try
        {
            await WaitForFixture(fixture); await Task.Delay(200);
            var entries = PreviewWindows.Capture().Where(e => e.Target.ProcessId == fixture.Id).ToArray();
            Assert(entries.Length == 2, "swap fixture has two exact window identities");
            var monitor = MonitorService.ForWindow(entries[0].Target.Handle, MonitorService.GetAll());
            // Restrict the test desktop to owned fixtures; never rearrange or suppress real user windows.
            var service = new WindowService(() => PreviewWindows.Capture().Where(e => e.Target.ProcessId == fixture.Id).ToArray());
            var originals = entries.Select(e => (e.Target, e.Bounds)).ToArray();
            var swapped = new[] { (entries[0].Target, entries[1].Bounds), (entries[1].Target, entries[0].Bounds) };
            var result = await service.ArrangeAsync(monitor, swapped, originals);
            Assert(result.Success, "overlapping unequal fixture frames swap exactly: " + result.Message);
            Assert(WindowService.VisibleBounds(entries[0].Target.Handle).Near(entries[1].Bounds) && WindowService.VisibleBounds(entries[1].Target.Handle).Near(entries[0].Bounds), "native swap exchanges exact original frames");
            Assert((await service.UndoAsync()).Success, "swap grouped Undo succeeds");
            Assert(entries.All(e => WindowService.VisibleBounds(e.Target.Handle).Near(e.Bounds)), "swap Undo restores both original rectangles");
            var stale = originals.Select((p, i) => i == 0 ? (p.Target, p.Bounds with { Left = p.Bounds.Left + 10 }) : p).ToArray();
            Assert(!(await service.ArrangeAsync(monitor, swapped, stale)).Success && entries.All(e => WindowService.VisibleBounds(e.Target.Handle).Near(e.Bounds)), "stale geometry guard rejects before any movement");
            var arbitraryOverlap = new[] { (entries[0].Target, entries[0].Bounds), (entries[1].Target, entries[0].Bounds) };
            Assert(!(await service.ArrangeAsync(monitor, arbitraryOverlap, originals)).Success, "exact-swap exception cannot admit arbitrary overlap");
            var initial = new[] { (entries[0].Target, PlacementMath.Calculate(monitor.Work, new(0, 0, .5, 1), 8, monitor.Dpi)), (entries[1].Target, PlacementMath.Calculate(monitor.Work, new(.5, 0, 1, 1), 8, monitor.Dpi)) };
            Assert((await service.ArrangeAsync(monitor, initial)).Success, "owned fixtures start in separate grid halves");
            var reflow = PlacementInteractions.Reflow(monitor.Work, "0", new(.5, 0, 1, .5), initial.Select((p, i) => new ReflowWindow(i.ToString(), p.Item2)).ToArray(), 6, 6, 8, monitor.Dpi);
            Assert(reflow.Success, "owned overlapping grid selection has a valid reflow plan");
            var moved = reflow.Slots.Select(s => (entries[int.Parse(s.Id)].Target, s.Bounds)).ToArray();
            Assert((await service.ArrangeAsync(monitor, moved, initial)).Success, "native reflow applies previewed group");
            Assert((await service.UndoAsync()).Success && initial.All(p => WindowService.VisibleBounds(p.Target.Handle).Near(p.Item2)), "reflow group Undo restores selected and displaced windows");

            // A previously absent catalog member is a new blocker even if its HWND already exists.
            Assert(!(await service.ArrangeAsync(monitor, [(entries[0].Target, initial[1].Item2)], [initial[0]])).Success,
                "new destination blocker rejects before movement");
            Assert(WindowService.VisibleBounds(entries[0].Target.Handle).Near(initial[0].Item2), "new blocker rejection leaves target unchanged");
            Native.ShowWindowAsync(entries[1].Target.Handle, 6); await Task.Delay(100);
            var hiddenCatalog = PreviewWindows.Capture().Where(e => e.Target.ProcessId == fixture.Id).Select(e => (e.Target, e.Bounds)).ToArray();
            Native.ShowWindowAsync(entries[1].Target.Handle, 9); await Task.Delay(120);
            Assert(!(await service.ArrangeAsync(monitor, [(entries[0].Target, initial[1].Item2)], hiddenCatalog)).Success,
                "restored destination blocker rejects from a previously hidden catalog");
            // Move only our second fixture away, then back into the planned rectangle during settling.
            var spare = PlacementMath.Calculate(monitor.Work, new(.5, .5, 1, 1), 8, monitor.Dpi);
            Assert((await service.ArrangeAsync(monitor, [(entries[1].Target, spare)])).Success, "owned blocker starts outside destination");
            var guarded = PreviewWindows.Capture().Where(e => e.Target.ProcessId == fixture.Id).Select(e => (e.Target, e.Bounds)).ToArray();
            var destinationRect = PlacementMath.Calculate(monitor.Work, new(.5, 0, 1, .5), 8, monitor.Dpi);
            async Task MoveBlocker()
            {
                await Task.Delay(60);
                Native.SetWindowPos(entries[1].Target.Handle, 0, destinationRect.Left, destinationRect.Top, destinationRect.Width, destinationRect.Height, 0x4014);
            }
            var arrival = MoveBlocker();
            var guardedResult = await service.ArrangeAsync(monitor, [(entries[0].Target, destinationRect)], guarded);
            await arrival;
            Assert(!guardedResult.Success && WindowService.VisibleBounds(entries[0].Target.Handle).Near(initial[0].Item2),
                "blocker entering during settling triggers verified rollback");

            // A new unrelated window outside the destination is not a reason to reject.
            Assert((await service.ArrangeAsync(monitor, [(entries[0].Target, initial[0].Item2)], [initial[0]])).Success,
                "new nonintersecting window does not reject placement");

            GridPilot.UI.Theme.Apply("Dark");
            var panel = new GridPilot.UI.GridPanel(entries[0].Target, new AppSettings(), true, (_, _) => null) { DismissOnDeactivate = false };
            panel.Show();
            panel.BeginPlacement(); panel.CompletePlacement(new(true), true);
            panel.Dismiss(false);
            await Task.Delay(100);
            Assert(!panel.IsVisible, "closing persistent panel during deferred preview capture cannot reopen it");
            var focusPanel = new GridPilot.UI.GridPanel(entries[0].Target, new AppSettings(), true, (_, _) => null);
            focusPanel.Show();
            // Finish initial Loaded/activation before modeling a user's focus switch.
            await focusPanel.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var panelHandle = new System.Windows.Interop.WindowInteropHelper(focusPanel).Handle;
            WaitForOwnedForeground(panelHandle, fixture);
            focusPanel.BeginPlacement();
            bool switched = Native.SetForegroundWindow(entries[1].Target.Handle);
            if (!switched)
            {
                Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint foregroundProcess);
                throw new InvalidOperationException($"NATIVE DESKTOP UNAVAILABLE: owned fixture activation was denied before refresh; foreground PID={foregroundProcess}.");
            }
            WaitForOwnedForeground(entries[1].Target.Handle, fixture);
            focusPanel.CompletePlacement(new(true), true); await Task.Delay(120);
            var foregroundAfterRefresh = Native.GetForegroundWindow();
            Native.GetWindowThreadProcessId(foregroundAfterRefresh, out uint processAfterRefresh);
            Assert(foregroundAfterRefresh == entries[1].Target.Handle,
                $"refresh preserves verified fixture foreground; expected={entries[1].Target.Handle}, actual={foregroundAfterRefresh}, PID={processAfterRefresh}, panel={panelHandle}, switchAccepted={switched}");
            // Model a second display's refreshed catalog without moving a window onto user hardware.
            var synthetic = monitor with { Device = "fixture-display-b", Bounds = new(monitor.Bounds.Right, monitor.Bounds.Top, monitor.Bounds.Right + monitor.Bounds.Width, monitor.Bounds.Bottom), Work = new(monitor.Work.Right, monitor.Work.Top, monitor.Work.Right + monitor.Work.Width, monitor.Work.Bottom) };
            var currentEntry = entries[0] with { Bounds = new(synthetic.Work.Left + 20, synthetic.Work.Top + 20, synthetic.Work.Left + 800, synthetic.Work.Top + 600) };
            var fields = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            typeof(GridPilot.UI.GridPanel).GetField("capturedWindows", fields)!.SetValue(focusPanel, new[] { currentEntry });
            var snapshots = (Dictionary<string, DesktopSnapshot>)typeof(GridPilot.UI.GridPanel).GetField("snapshots", fields)!.GetValue(focusPanel)!;
            snapshots[synthetic.Device] = new(synthetic, null);
            var picker = (System.Windows.Controls.ComboBox)focusPanel.FindName("MonitorPicker");
            picker.ItemsSource = new[] { monitor, synthetic }; picker.SelectedItem = monitor;
            focusPanel.SelectSidebarWindow(entries[0]);
            Assert(((DisplayMonitor)picker.SelectedItem).Device == synthetic.Device, "sidebar routes using refreshed entry before choosing its display");
            focusPanel.Dismiss(false);
        }
        finally { if (!fixture.HasExited) { fixture.Kill(); await fixture.WaitForExitAsync(); } }
    }
    private static async Task PendingPlacementRecoveryTests()
    {
        foreach (bool beyondBound in new[] { false, true })
        {
            using var delayed = StartFixture(beyondBound ? "--very-slow-batch-resize" : "--slow-batch-resize");
            using var blocker = StartFixture();
            try
            {
                var hwnd = await WaitForFixture(delayed); var blockerHandle = await WaitForFixture(blocker); await Task.Delay(100);
                var target = WindowService.Capture(hwnd, out _)!;
                var monitor = MonitorService.ForWindow(hwnd, MonitorService.GetAll());
                var original = WindowService.VisibleBounds(hwnd);
                var destination = new PixelRect(original.Left, original.Top, original.Left + Math.Max(320, original.Width - 180), original.Top + Math.Max(240, original.Height - 100));
                Native.SetWindowPos(blockerHandle, 0, monitor.Work.Left + 16, monitor.Work.Top + 16, 420, 300, 0x4014); await Task.Delay(100);
                IReadOnlyList<PreviewWindowEntry> CaptureOwned() => PreviewWindows.Capture().Where(e => e.Target.ProcessId == delayed.Id || e.Target.ProcessId == blocker.Id).ToArray();
                var guards = CaptureOwned().Select(e => (e.Target, e.Bounds)).ToArray();
                var service = new WindowService(CaptureOwned);
                var operation = service.ArrangeAsync(monitor, [(target, destination)], guards);
                await Task.Delay(100);
                Native.SetWindowPos(blockerHandle, 0, destination.Left, destination.Top, 420, 300, 0x4014);
                await Task.Delay(250);
                Assert(service.Busy && !operation.IsCompleted, "occupancy cancellation waits while the forward resize is still pending");
                var result = await operation;
                Assert(!result.Success, "late blocker cancels slow native placement");
                if (beyondBound)
                {
                    Assert(service.CanUndo && result.Message!.Contains("could not be verified"), "timeout retains uncertain recovery instead of claiming unchanged bounds prove rollback");
                    Assert(!(await service.PlaceAsync(target, monitor.Device, Region.Full, 8)).Success, "unverified queued request blocks new placements");
                    var recovery = service.UndoAsync();
                    await Task.Delay(100);
                    Assert(service.CanUndo && service.Busy && !recovery.IsCompleted, "immediate Undo cannot consume uncertain snapshot while native request is pending");
                    var recovered = await recovery;
                    if (!recovered.Success)
                    {
                        Assert(service.CanUndo, "a bounded Undo that cannot prove responsiveness retains recovery");
                        await Task.Delay(1200);
                        recovered = await service.UndoAsync();
                    }
                    Assert(recovered.Success && !service.CanUndo, "Undo consumes recovery only after an observed native response and verified restoration: " + recovered.Message);
                }
                else Assert(!service.CanUndo, "quiesced slow request rolls back without retaining an unnecessary retry");
                await Task.Delay(1200);
                Assert(WindowService.VisibleBounds(hwnd).Near(original), "no queued forward resize escapes after failure/recovery was reported");
            }
            finally
            {
                foreach (var process in new[] { delayed, blocker }) if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
            }
        }
    }
    private static async Task ConstrainedArrangementRetryTest()
    {
        using var fixture = StartFixture("--constrained");
        try
        {
            var hwnd = await WaitForFixture(fixture); await Task.Delay(100);
            var target = WindowService.Capture(hwnd, out _)!;
            var monitor = MonitorService.ForWindow(hwnd, MonitorService.GetAll());
            var original = WindowService.VisibleBounds(hwnd);
            var request = new PixelRect(monitor.Work.Left + 30, monitor.Work.Top + 30, monitor.Work.Left + 350, monitor.Work.Top + 270);
            var service = new WindowService(() => PreviewWindows.Capture().Where(e => e.Target.ProcessId == fixture.Id).ToArray());
            var rejected = await service.ArrangeAsync(monitor, [(target, request)]);
            Assert(!rejected.Success && rejected.Constraint is not null && !service.CanUndo, "responsive size refusal automatically verifies rollback without requiring Undo: " + rejected.Message);
            Assert(WindowService.VisibleBounds(hwnd).Near(original), "automatic recovery restores original bounds");
            var constraint = rejected.Constraint!;
            Assert(constraint.Target == target && constraint.MinimumWidth > request.Width && constraint.MinimumHeight > request.Height, "constraint retains exact HWND/PID and native minimum response");
            var plan = SmartArrangement.Calculate(monitor.Work, [new("fixture", 0, constraint.MinimumWidth, constraint.MinimumHeight, ArrangementRole.Companion)], 8, monitor.Dpi);
            Assert(plan.Success, "learned constraint creates a feasible new preview");
            var accepted = await service.ArrangeAsync(monitor, [(target, plan.Slots[0].Bounds)]);
            Assert(accepted.Success, "next arrangement succeeds without an intervening Undo: " + accepted.Message);
            await Task.Delay(400);
            Assert(WindowService.VisibleBounds(hwnd).Near(plan.Slots[0].Bounds), "accepted larger frame stays in place");
            Assert((await service.UndoAsync()).Success && WindowService.VisibleBounds(hwnd).Near(original), "successful retry keeps real grouped Undo");
        }
        finally { if (!fixture.HasExited) { fixture.Kill(); await fixture.WaitForExitAsync(); } }
    }

    private static async Task MinimumPlacementRecoveryTest()
    {
        using var fixture = StartFixture("--constrained");
        try
        {
            var hwnd = await WaitForFixture(fixture); await Task.Delay(100);
            var target = WindowService.Capture(hwnd, out _)!;
            var monitor = MonitorService.ForWindow(hwnd, MonitorService.GetAll());
            var initial = WindowService.VisibleBounds(hwnd);
            // Windows clamps this owned fixture to its real WPF minimum, in destination DPI.
            Native.SetWindowPos(hwnd, 0, initial.Left, initial.Top, 1, 1, 0x4014); await Task.Delay(180);
            var minimum = WindowService.VisibleBounds(hwnd);
            Assert(minimum.Width > 320 && minimum.Height > 240, "native fixture enforces a minimum larger than the rejected request");
            var service = new WindowService(() => PreviewWindows.Capture().Where(e => e.Target.ProcessId == fixture.Id).ToArray());
            var rejected = await service.ArrangeAsync(monitor, [(target, new(minimum.Left, minimum.Top, minimum.Left + 1, minimum.Top + 1))], [(target, minimum)]);
            Assert(!rejected.Success && service.CanUndo, "undersized request retains recovery for an unchanged minimum-sized window");
            var recovered = await service.UndoAsync();
            Assert(recovered.Success && !service.CanUndo && WindowService.VisibleBounds(hwnd).Near(minimum), "minimum-compatible Undo acknowledges and restores exact original frame: " + recovered.Message);
            await Task.Delay(300);
            Assert(WindowService.VisibleBounds(hwnd).Near(minimum), "minimum-sized recovery leaves no queued probe or off-screen frame");
            Assert((await service.ArrangeAsync(monitor, [(target, minimum)], [(target, minimum)])).Success, "recovered minimum-sized window permits subsequent valid placement");
        }
        finally { if (!fixture.HasExited) { fixture.Kill(); await fixture.WaitForExitAsync(); } }
    }
}
