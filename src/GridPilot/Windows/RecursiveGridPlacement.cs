namespace GridPilot.Windows;

internal sealed record RecursiveGridPreflight(WindowTarget Target, DisplayMonitor Display,
    PlacementConstraint Minimum, bool UsedConservativeFallback, PixelRect OriginalVisible, bool WasMaximized);

internal sealed record RecursiveGridPreflightResult(RecursiveGridPreflight? Preflight, string? Message = null);

internal static class RecursiveGridPlacement
{
    private sealed record State(DisplayMonitor Display, PixelRect Outer, PixelRect Visible,
        Native.WindowPlacement Placement, uint WindowDpi, nint Style, bool Maximized);

    internal static async Task<RecursiveGridPreflightResult> PrepareAsync(WindowTarget target, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (ReadState(target, out string? error) is not { } before) return new(null, error);
        var detected = await WindowMinimums.ReadAsync(target, before.Display, cancellation);
        cancellation.ThrowIfCancellationRequested();
        if (ReadState(target, out error) is not { } after) return new(null, error);
        if (!SameState(before, after)) return new(null, "The target window or display changed while its minimum size was checked. Open the grid again.");

        int invisibleWidth = before.Maximized ? 0 : Math.Max(0, before.Outer.Width - before.Visible.Width);
        int invisibleHeight = before.Maximized ? 0 : Math.Max(0, before.Outer.Height - before.Visible.Height);
        PlacementConstraint? system;
        using (new Native.DpiScope())
            system = WindowMinimums.ToVisible(target,
                Native.GetSystemMetricsForDpi(34, before.WindowDpi), Native.GetSystemMetricsForDpi(35, before.WindowDpi),
                before.WindowDpi, before.Display.Dpi, invisibleWidth, invisibleHeight);

        // DWM exposes the current frame only. Without restoring a maximized target,
        // its saved outer size is a conservative upper bound for its restored visible size.
        var restored = before.Maximized ? before.Placement.Normal.Pixels : before.Visible;
        var minimum = CombineMinimum(target, system, detected, restored);
        if (minimum is null) return new(null, "Windows could not determine a safe minimum size for this window.");
        if (minimum.MinimumWidth > before.Display.Work.Width || minimum.MinimumHeight > before.Display.Work.Height)
            return new(null, "This window's supported minimum size does not fit the display's usable area.");
        return new(new(target, before.Display, minimum, detected is null, before.Visible, before.Maximized));
    }

    internal static PlacementConstraint? CombineMinimum(WindowTarget target, PlacementConstraint? system,
        PlacementConstraint? detected, PixelRect restored)
    {
        static bool Valid(PlacementConstraint? constraint) => constraint is { MinimumWidth: > 0 and <= 32768, MinimumHeight: > 0 and <= 32768 };
        if (!Valid(system)) return null;
        if (detected is not null && (!Valid(detected) || detected.Target != target)) detected = null;
        if (system!.Target != target) return null;
        int width = detected?.MinimumWidth ?? restored.Width;
        int height = detected?.MinimumHeight ?? restored.Height;
        if (width is <= 0 or > 32768 || height is <= 0 or > 32768) return null;
        return new(target, Math.Max(system.MinimumWidth, width), Math.Max(system.MinimumHeight, height));
    }

    internal static async Task<PlacementResult> PlaceAsync(WindowService service, RecursiveGridPreflight preflight, PixelRect bounds)
    {
        if (service.Busy) return new(false, "A placement is already in progress.");
        if (!Fits(bounds, preflight.Display.Work, preflight.Minimum))
            return new(false, "This cell is smaller than the window's supported minimum or outside the usable display area.");
        var fresh = await PrepareAsync(preflight.Target);
        if (fresh.Preflight is not { } current) return new(false, fresh.Message);
        if (!SameDisplay(preflight.Display, current.Display) || current.WasMaximized != preflight.WasMaximized ||
            current.OriginalVisible != preflight.OriginalVisible)
            return new(false, "The target window or display changed. Open the grid again.");
        if (!Fits(bounds, current.Display.Work, current.Minimum))
            return new(false, "This window's minimum size changed. Select a larger cell or open the grid again.", current.Minimum);

        var result = await service.ArrangeAsync(current.Display, [(current.Target, bounds)],
            verify: () =>
            {
                if (!WindowService.IsSameWindow(current.Target) || WindowService.Capture(current.Target.Handle, out _) is null)
                    throw new InvalidOperationException("The target window is no longer available or cannot be resized.");
            },
            verifyResult: () =>
            {
                if (WindowService.VisibleBounds(current.Target.Handle) != bounds)
                    throw new InvalidOperationException("This application did not accept the exact selected cell. Select a larger cell or another window.");
            });
        if (result.Success && WindowService.IsSameWindow(current.Target))
        {
            Native.SetForegroundWindow(current.Target.Handle);
            return result with { Message = "Window placed. Undo is available from the tray." };
        }
        return result;
    }

    private static bool Fits(PixelRect bounds, PixelRect work, PlacementConstraint minimum) => bounds.IsValid &&
        bounds.Width >= minimum.MinimumWidth && bounds.Height >= minimum.MinimumHeight &&
        bounds.Left >= work.Left && bounds.Top >= work.Top && bounds.Right <= work.Right && bounds.Bottom <= work.Bottom;

    private static bool SameDisplay(DisplayMonitor first, DisplayMonitor second) => first.Handle == second.Handle &&
        first.Device == second.Device && first.Bounds == second.Bounds && first.Work == second.Work && first.Dpi == second.Dpi;

    private static bool SameState(State before, State after) => SameDisplay(before.Display, after.Display) &&
        before.Outer == after.Outer && before.Visible == after.Visible && before.WindowDpi == after.WindowDpi &&
        before.Style == after.Style && before.Maximized == after.Maximized &&
        before.Placement.Normal.Pixels == after.Placement.Normal.Pixels && before.Placement.ShowCommand == after.Placement.ShowCommand;

    private static State? ReadState(WindowTarget target, out string? error)
    {
        error = null;
        if (!WindowService.IsSameWindow(target)) { error = "The target window is no longer available."; return null; }
        if (WindowService.Capture(target.Handle, out error) is null) return null;
        using var dpi = new Native.DpiScope();
        var placement = Native.WindowPlacement.Create();
        uint windowDpi = Native.GetDpiForWindow(target.Handle);
        if (windowDpi == 0 || !Native.GetWindowPlacement(target.Handle, ref placement) || !Native.GetWindowRect(target.Handle, out var outer))
        { error = "Windows could not read this window's placement."; return null; }
        try
        {
            var display = MonitorService.ForWindow(target.Handle, MonitorService.GetAll());
            if (!outer.Pixels.IsValid || !display.Work.IsValid) throw new InvalidOperationException("The target window or display is unavailable.");
            return new(display, outer.Pixels, WindowService.VisibleBounds(target.Handle), placement, windowDpi,
                Native.GetWindowLongPtr(target.Handle, -16), Native.IsZoomed(target.Handle));
        }
        catch (InvalidOperationException ex) { error = ex.Message; return null; }
    }
}
