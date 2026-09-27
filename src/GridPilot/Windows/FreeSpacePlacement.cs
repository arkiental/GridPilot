namespace GridPilot.Windows;

internal sealed record FreeSpaceSnapshot(WindowTarget Target, DisplayMonitor Monitor, FreeSpacePlan Plan,
    IReadOnlyList<(WindowTarget Target, PixelRect Bounds)> Originals, int MinimumWidth, int MinimumHeight, double Ratio);

internal static class FreeSpacePlacement
{
    internal static async Task<FreeSpaceSnapshot> CaptureAsync(WindowTarget target, string device, AppSettings settings,
        CancellationToken cancellation = default, Func<IReadOnlyList<PreviewWindowEntry>>? capture = null, PlacementConstraint? observed = null)
    {
        return await Task.Run(async () =>
        {
            cancellation.ThrowIfCancellationRequested();
            if (!WindowService.IsSameWindow(target) || WindowService.Capture(target.Handle, out _) is null)
                throw new InvalidOperationException("The selected window is unavailable or cannot be resized.");
            var monitor = MonitorService.GetAll().FirstOrDefault(m => m.Device == device)
                ?? throw new InvalidOperationException("The selected display was disconnected. Choose a display again.");
            var id = ApplicationIdentity.Read(target);
            var preference = settings.ArrangementPreferences.FirstOrDefault(p => string.Equals(p.AppId, id, StringComparison.OrdinalIgnoreCase));
            var native = await WindowMinimums.ReadAsync(target, monitor, cancellation);
            int width = Math.Max(preference?.MinimumWidth ?? 1, native?.MinimumWidth ?? 1);
            int height = Math.Max(preference?.MinimumHeight ?? 1, native?.MinimumHeight ?? 1);
            if (observed is not null && Same(target, observed.Target))
            { width = Math.Max(width, observed.MinimumWidth); height = Math.Max(height, observed.MinimumHeight); }
            double ratio = PartialWorkspacePlacement.EffectiveRatio(preference, monitor.Work, settings.Columns, settings.Rows);
            var entries = capture?.Invoke() ?? PreviewWindows.Capture(true, cancellation);
            var originals = entries.Where(e => Same(e.Target, target) || PartialWorkspace.Overlaps(e.Bounds, monitor.Work))
                .Select(e => (e.Target, e.Bounds)).ToList();
            if (!originals.Any(e => Same(e.Target, target))) throw new InvalidOperationException("The selected window is no longer visible.");
            var plan = FreeSpace.Find(monitor.Work, monitor.Dpi, settings.Gap,
                originals.Where(e => !Same(e.Target, target)).Select(e => e.Bounds).ToArray(), width, height, ratio, cancellation);
            return new FreeSpaceSnapshot(target, monitor, plan, originals, width, height, ratio);
        }, cancellation);
    }

    internal static bool Same(WindowTarget a, WindowTarget b) => a.Handle == b.Handle && a.ProcessId == b.ProcessId;
    internal static bool Matches(FreeSpaceSnapshot a, FreeSpaceSnapshot b) => Same(a.Target, b.Target) &&
        a.Monitor.Device == b.Monitor.Device && a.Monitor.Handle == b.Monitor.Handle && a.Monitor.Work == b.Monitor.Work && a.Monitor.Dpi == b.Monitor.Dpi &&
        a.MinimumWidth == b.MinimumWidth && a.MinimumHeight == b.MinimumHeight && a.Ratio == b.Ratio &&
        a.Originals.Count == b.Originals.Count && a.Originals.All(x => b.Originals.Any(y => Same(x.Target, y.Target) && x.Bounds == y.Bounds));

    internal static void VerifyOccupancy(FreeSpaceSnapshot snapshot, PixelRect frame, double gap,
        Func<IReadOnlyList<PreviewWindowEntry>>? capture = null)
    {
        var current = MonitorService.GetAll().FirstOrDefault(m => m.Device == snapshot.Monitor.Device);
        if (current is null || current.Handle != snapshot.Monitor.Handle || current.Work != snapshot.Monitor.Work || current.Dpi != snapshot.Monitor.Dpi)
            throw new InvalidOperationException("The selected display changed. Review a new free space preview.");
        var obstacles = (capture?.Invoke() ?? PreviewWindows.Capture(true)).Where(e => !Same(e.Target, snapshot.Target) && PartialWorkspace.Overlaps(e.Bounds, current.Work)).ToArray();
        var expected = snapshot.Originals.Where(e => !Same(e.Target, snapshot.Target)).ToArray();
        if (obstacles.Length != expected.Length || obstacles.Any(e => !expected.Any(o => Same(e.Target, o.Target) && e.Bounds == o.Bounds)))
            throw new InvalidOperationException("The window arrangement changed. Review the updated preview.");
        var inset = PlacementMath.Calculate(current.Work, Region.Full, gap, current.Dpi);
        if (frame.Width < snapshot.MinimumWidth || frame.Height < snapshot.MinimumHeight ||
            snapshot.Ratio > 0 && Math.Abs(frame.Height - frame.Width / snapshot.Ratio) > 1)
            throw new InvalidOperationException("The actual window frame did not meet its size requirements.");
        int gutter = inset.Left - current.Work.Left;
        // Check the actual frame as well as the request, including the configured gutter.
        var expanded = new PixelRect(frame.Left - gutter, frame.Top - gutter, frame.Right + gutter, frame.Bottom + gutter);
        if (frame.Left < inset.Left || frame.Top < inset.Top || frame.Right > inset.Right || frame.Bottom > inset.Bottom ||
            obstacles.Any(e => PartialWorkspace.Overlaps(expanded, e.Bounds)))
            throw new InvalidOperationException("The actual window frame no longer fits the verified free space.");
    }
}
