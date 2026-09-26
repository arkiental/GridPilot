using GridPilot.Core;
using GridPilot.Windows;
using GridPilot.UI;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace GridPilot.Tests;

internal static partial class Program
{
    private static async Task ArrangementNativeTests()
    {
        using var fixture = StartFixture("--pair");
        try
        {
            await WaitForFixture(fixture);
            IReadOnlyList<PreviewWindowEntry> entries = [];
            for (int i = 0; i < 40; i++)
            {
                entries = PreviewWindows.Capture().Where(e => e.Target.ProcessId == fixture.Id).ToArray();
                if (entries.Count == 2) break;
                await Task.Delay(25);
            }
            Assert(entries.Count == 2, "batch fixture has two windows");
            var monitor = MonitorService.ForWindow(entries[0].Target.Handle, MonitorService.GetAll());
            var before = entries.Select(e => WindowService.VisibleBounds(e.Target.Handle)).ToArray();
            var slots = new[] { PlacementMath.Calculate(monitor.Work, new(0, 0, .5, 1), 8, monitor.Dpi), PlacementMath.Calculate(monitor.Work, new(.5, 0, 1, 1), 8, monitor.Dpi) };
            var service = new WindowService();
            var result = await service.ArrangeAsync(monitor, entries.Select((e, i) => (e.Target, slots[i])).ToArray());
            Assert(result.Success && service.CanUndo, "batch applies and creates grouped undo: " + result.Message);
            for (int i = 0; i < 2; i++) Assert(WindowService.VisibleBounds(entries[i].Target.Handle).Near(slots[i]), "batch exact visible bounds");
            Assert((await service.UndoAsync()).Success && !service.CanUndo, "batch undo consumed");
            for (int i = 0; i < 2; i++) Assert(WindowService.VisibleBounds(entries[i].Target.Handle).Near(before[i]), "batch restores original geometry");
            // Force the second fixture below its system minimum, after the first has moved.
            result = await service.ArrangeAsync(monitor, [(entries[0].Target, slots[0]), (entries[1].Target, new(monitor.Work.Left + 10, monitor.Work.Top + 10, monitor.Work.Left + 11, monitor.Work.Top + 11))]);
            Assert(!result.Success, "batch refuses app-constrained overlap instead of growing tiles");
            for (int i = 0; i < 2; i++) Assert(WindowService.VisibleBounds(entries[i].Target.Handle).Near(before[i]), "failed batch rolls all windows back");
            Assert(Windows.ApplicationIdentity.Read(entries[0].Target) == Windows.ApplicationIdentity.Read(entries[1].Target), "same app windows share persistent rule identity");
            ArrangementSheetTests(monitor, entries);
            await ArrangementPreviewNativeTests(monitor, entries);
        }
        finally { if (!fixture.HasExited) { fixture.Kill(); await fixture.WaitForExitAsync(); } }
    }
    private static void ArrangementSheetTests(DisplayMonitor monitor, IReadOnlyList<PreviewWindowEntry> entries)
    {
        static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
        foreach (bool remember in new[] { false, true })
        {
            bool applied = false; AppSettings? saved = null;
            var settings = new AppSettings { Columns = 2, Rows = 2 };
            var sheet = new AutoArrangeWindow(monitor, settings, value => { saved = value.Clone(); return null; }, (_, _) => applied = true, entries);
            var rowList = Field<System.Collections.IList>(sheet, "rows");
            object first = rowList[0]!;
            T RowValue<T>(string name) => (T)first.GetType().GetProperty(name)!.GetValue(first)!;
            var include = RowValue<CheckBox>("Include");
            include.IsChecked = false;
            RowValue<TextBox>("Ratio").Text = "unfinished:";
            RowValue<TextBox>("GridShape").Text = "6x6";
            Field<CheckBox>(sheet, "remember").IsChecked = remember;
            var button = Field<Button>(sheet, "applyButton");
            Assert(button.IsEnabled, "excluded invalid or stale rules do not block preview");
            include.IsChecked = true;
            Assert(!button.IsEnabled, "included invalid rules still block preview");
            include.IsChecked = false;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(applied, "arrangement sheet applies with excluded unfinished fields");
            Assert(remember ? saved?.ArrangementPreferences.Count == 1 && saved.Validate() is null : saved is null, "remember path only saves valid included app rules");
            sheet.Close();
        }
        var appId = Windows.ApplicationIdentity.Read(entries[0].Target)!;
        var remembered = new AppSettings();
        var savedPlace = new ArrangementPlace { Monitor = monitor.Device, Region = new(0, 0, .5, 1) };
        remembered.ArrangementPreferences.Add(new() { AppId = appId, Places = [savedPlace] });
        var ambiguity = new AutoArrangeWindow(monitor, remembered, _ => null, (_, _) => { }, entries);
        Assert(!Field<Button>(ambiguity, "applyButton").IsEnabled, "duplicate app windows require explicit saved-slot assignment");
        Assert((bool)ambiguity.GetType().GetMethod("SaveDraft", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(ambiguity, null)! && Field<AppSettings>(ambiguity, "settings").ArrangementPreferences[0].Places.Count == 1, "save-only preserves an unresolved app place without assigning a sibling");
        var duplicateRows = Field<System.Collections.IList>(ambiguity, "rows");
        ambiguity.GetType().GetMethod("AssignPlace", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(ambiguity, [duplicateRows[1], savedPlace]);
        ambiguity.GetType().GetMethod("Recalculate", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(ambiguity, null);
        Assert(Field<Button>(ambiguity, "applyButton").IsEnabled, "explicit exact-window saved-slot choice resolves ambiguity");
        var assignedPlan = Field<ArrangementPlan>(ambiguity, "plan");
        Assert(assignedPlan.Slots.Single(s => s.Id == "2").Bounds == PlacementMath.Calculate(monitor.Work, savedPlace.Region, remembered.Gap, monitor.Dpi), "chosen sibling receives exact saved place");
        Assert(duplicateRows[0]!.GetType().GetProperty("Place")!.GetValue(duplicateRows[0]) is null, "other same-app sibling stays automatic");
        ambiguity.GetType().GetMethod("ClearPlace", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(ambiguity, [duplicateRows[1]]);
        ambiguity.GetType().GetMethod("Recalculate", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(ambiguity, null);
        Assert(Field<Button>(ambiguity, "applyButton").IsEnabled && Field<AppSettings>(ambiguity, "settings").ArrangementPreferences[0].Places.Count == 0, "clear place removes reservation and remembered assignment in draft");
        var staleAssignment = (Button)duplicateRows[0]!.GetType().GetProperty("UseSaved")!.GetValue(duplicateRows[0])!;
        Assert(!staleAssignment.IsEnabled, "saved-slot assignment control is disabled after clear");
        staleAssignment.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(duplicateRows[0]!.GetType().GetProperty("Place")!.GetValue(duplicateRows[0]) is null, "stale control event cannot resurrect a cleared place");
        Assert(remembered.ArrangementPreferences[0].Places.Count == 1, "draft edits never mutate caller settings before save");
        ambiguity.Close();
        var editorOwner = new AutoArrangeWindow(monitor, new(), _ => null, (_, _) => { }, entries);
        _ = new System.Windows.Interop.WindowInteropHelper(editorOwner).EnsureHandle();
        var editor = new ArrangementPlaceEditor(editorOwner, monitor, new(), "Owned fixture", null, [], 1);
        void Edit(string method) => editor.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(editor, null);
        Field<TextBox>(editor, "cells").Text = "1,1,1,1"; Edit("SelectCells");
        Assert(Field<Region>(editor, "selection") == new Region(0, 0, 1d / 6, 1d / 6), "numeric editor selects exact outer cell");
        Edit("CreateArea");
        Assert(editor.Areas.Count == 1 && Field<ArrangementArea>(editor, "area").Columns == 2, "subdivide creates and zooms into local area");
        Field<TextBox>(editor, "cells").Text = "2,1,1,2"; Edit("SelectCells");
        Assert(Field<Region>(editor, "selection") == new Region(1d / 12, 0, 1d / 6, 1d / 6), "numeric editor maps child cells through parent");
        var selectedChild = Field<Region>(editor, "selection");
        editor.GetType().GetMethod("BeginDrawing", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(editor, [new Point(0, 0)]);
        Assert(Field<Region>(editor, "selection") != selectedChild, "new drag previews its tentative cells");
        Edit("CancelDrawing");
        Assert(Field<Region>(editor, "selection") == selectedChild, "cancelled drag restores the previous child selection");
        Assert(Field<TextBox>(editor, "cells").Text == "2,1,1,2", "cancelled drag keeps numeric cells consistent with restored selection");
        if (Environment.GetCommandLineArgs().Contains("--inspect-arrangement"))
        {
            void Render(Window window, string label)
            {
                window.ShowActivated = false; window.Show(); window.UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"GridPilot-{label}-{Environment.ProcessId}.png");
                using var file = System.IO.File.Create(path); encoder.Save(file); Console.WriteLine("Owned WPF review image: " + path);
            }
            Render(editorOwner, "arrangement"); Render(editor, "subgrid");
        }
        Field<TextBox>(editor, "cells").Text = "3,1,1,1"; Edit("SelectCells");
        Assert(Field<Region>(editor, "selection") == selectedChild, "out-of-range child cells cannot alter selection");
        Edit("RemoveArea"); Assert(editor.Areas.Count == 0, "empty local area can be removed explicitly");
        editor.Close(); editorOwner.Close();
        AppSettings? savedWithoutWindows = null;
        var reservedOnly = new AppSettings { ArrangementAreas = [new() { Monitor = monitor.Device, Region = Region.Full }] };
        var empty = new AutoArrangeWindow(monitor, reservedOnly, value => { savedWithoutWindows = value.Clone(); return null; }, (_, _) => throw new InvalidOperationException("must not move windows"), []);
        Assert(!Field<Button>(empty, "applyButton").IsEnabled, "empty catalog cannot arrange");
        Assert((bool)empty.GetType().GetMethod("SaveDraft", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(empty, null)! && savedWithoutWindows?.ArrangementAreas.Count == 1, "reserved-only configuration saves with no live app or feasible movement plan");
        empty.Close();
        var highResolution = monitor with { Work = new(0, 0, 7680, 4320), Dpi = 96 };
        var autoScale = new AutoArrangeWindow(highResolution, new(), _ => null, (_, _) => { }, entries);
        Assert(Field<double>(autoScale, "uiScale") == 2, "8K100% uses larger interface independently of placement DPI");
        autoScale.Close();
        var manualScale = new AutoArrangeWindow(highResolution, new() { ArrangementUiScale = 1 }, _ => null, (_, _) => { }, entries);
        Assert(Field<double>(manualScale, "uiScale") == 1, "explicit UI sizing overrides large-display automatic suggestion");
        manualScale.Close();
        var parentA = new ArrangementArea { Monitor = monitor.Device, Region = new(0, 0, .5, 1) };
        var parentB = new ArrangementArea { Monitor = monitor.Device, Region = new(.5, 0, 1, 1) };
        var moveSettings = new AppSettings { ArrangementAreas = [parentA, parentB], ArrangementPreferences = [new() { AppId = appId, Places = [new() { Monitor = monitor.Device, AreaId = parentB.Id, Region = parentB.Region }] }] };
        AppSettings? afterRemoval = null;
        var movedDraft = new AutoArrangeWindow(monitor, moveSettings, value => { afterRemoval = value.Clone(); return null; }, (_, _) => { }, entries);
        var movedRows = Field<System.Collections.IList>(movedDraft, "rows");
        movedDraft.GetType().GetMethod("AssignPlace", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(movedDraft, [movedRows[0], new ArrangementPlace { Monitor = monitor.Device, AreaId = parentA.Id, Region = parentA.Region }]);
        movedDraft.GetType().GetMethod("RemoveReservedArea", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(movedDraft, [Field<AppSettings>(movedDraft, "settings").ArrangementAreas.Single(a => a.Id == parentA.Id)]);
        Assert(Field<AppSettings>(movedDraft, "settings").ArrangementPreferences[0].Places.Count == 0, "removing draft parent clears superseded saved slot in old parent");
        Assert((bool)movedDraft.GetType().GetMethod("SaveDraft", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(movedDraft, null)! && afterRemoval?.ArrangementPreferences[0].Places.Count == 0, "save/reopen cannot resurrect the old parent slot");
        Assert(afterRemoval!.ArrangementAreas.Count == 1 && afterRemoval.ArrangementAreas[0].Id == parentB.Id, "unrelated old parent remains explicitly reserved");
        movedDraft.Close();
        var narrow = new AutoArrangeWindow(monitor, new() { ArrangementUiScale = 2.5 }, _ => null, (_, _) => { }, entries);
        var viewport = (FrameworkElement)narrow.Content;
        viewport.Measure(new Size(700, 500)); viewport.Arrange(new Rect(0, 0, 700, 500)); viewport.UpdateLayout();
        var action = Field<Button>(narrow, "applyButton");
        var actionBounds = action.TransformToAncestor(viewport).TransformBounds(new Rect(action.RenderSize));
        Assert(actionBounds.Left >= 0 && actionBounds.Right <= 701, "250% footer action remains horizontally reachable in a narrow viewport");
        narrow.Close();
    }
    private static void ArrangementTests()
    {
        var attempted = new List<int>();
        var failed = WindowService.RestoreGroupAsync(new[] { 1, 2, 3, 4 }, i =>
        {
            attempted.Add(i);
            if (i == 1) throw new InvalidOperationException("window disappeared during restore");
            if (i == 3) throw new ArgumentException("topology changed during restore");
            return Task.FromResult(i != 2);
        }).GetAwaiter().GetResult();
        Assert(attempted.SequenceEqual(new[] { 1, 2, 3, 4 }), "restoration continues after per-window exceptions");
        Assert(failed.SequenceEqual(new[] { 1, 2, 3 }), "uncertain and failed snapshots retained for retry");
        foreach (int count in new[] { 1, 2, 3, 5, 8, 16, 32 })
        foreach (uint dpi in new uint[] { 96, 144, 192 })
        {
            var work = new PixelRect(-3840, -120, 0, 2040);
            var items = Enumerable.Range(0, count).Select(i => new ArrangementItem(i.ToString(), i % 3 == 0 ? 16d / 9 : 0, 120, 80)).ToArray();
            var plan = AutoArrangement.Calculate(work, items, 8, dpi);
            Assert(plan.Success && plan.Slots.Count == count, "arrangement supports count and DPI");
            Assert(plan.Slots.SequenceEqual(AutoArrangement.Calculate(work, items, 8, dpi).Slots), "deterministic arrangement");
            foreach (var slot in plan.Slots)
            {
                var r = slot.Bounds; var item = items.Single(i => i.Id == slot.Id);
                Assert(r.Left >= work.Left && r.Right <= work.Right && r.Top >= work.Top && r.Bottom <= work.Bottom, "tile inside negative-origin work area");
                Assert(r.Width >= item.MinimumWidth && r.Height >= item.MinimumHeight, "tile obeys minimum");
                Assert(item.AspectRatio == 0 || Math.Abs(r.Height - r.Width / item.AspectRatio) <= 1, "locked aspect within pixel rounding");
                Assert(plan.Slots.Where(s => s.Id != slot.Id).All(s => r.Right <= s.Bounds.Left || r.Left >= s.Bounds.Right || r.Bottom <= s.Bounds.Top || r.Top >= s.Bounds.Bottom), "tiles do not overlap");
            }
        }
        var flexible = AutoArrangement.Calculate(new(0, 0, 1920, 1080), Enumerable.Range(0, 7).Select(i => new ArrangementItem(i.ToString(), 0, 1, 1)).ToArray(), 0, 96);
        Assert(flexible.Slots.Sum(s => (long)s.Bounds.Width * s.Bounds.Height) == 1920L * 1080, "flexible arrangement fills every pixel");
        foreach (double gap in new[] { 0d, 8, 13 })
        {
            var uneven = AutoArrangement.Calculate(new(0, 0, 1920, 1080), [new("large", 0, 1280, 720), new("small", 0, 320, 240)], gap, 96);
            Assert(uneven.Success && uneven.Slots.Single(s => s.Id == "large").Bounds.Width >= 1280, "reserve unequal minimum widths before distributing remaining space");
            Assert(uneven.Slots.Sum(s => s.Bounds.Width) == 1920 - 3 * gap, "unequal minimum shelf fills display apart from gutters");
        }
        Assert(!AutoArrangement.Calculate(new(0, 0, 100, 100), [new("x", 0, 101, 100)], 0, 96).Success, "infeasible minimum rejected");
        Assert(!AutoArrangement.Calculate(new(0, 0, 100, 100), [], 0, 96).Success, "empty selection rejected");
        Assert(!AutoArrangement.Calculate(new(0, 0, 100, 100), [new("x", double.NaN, 1, 1)], 0, 96).Success, "nonfinite ratio rejected");
        var settings = new AppSettings(); settings.ArrangementPreferences.Add(new() { AppId = "exe:test", AspectRatio = 16d / 9, MinimumWidth = 640 });
        Assert(settings.Clone().ArrangementPreferences[0].MinimumWidth == 640 && settings.Validate() is null, "app rules persist through settings serialization");
        settings.ArrangementPreferences.Add(new() { AppId = "EXE:TEST" }); Assert(settings.Validate() is not null, "duplicate executable rules rejected");
        SmartArrangementTests();
        ArrangementSplitTests();
    }

    private static void SmartArrangementTests()
    {
        ArrangementItem[] named = [new("blender", 0, 320, 240, ArrangementRole.Workspace), new("chrome", 0, 320, 240), new("explorer", 0, 320, 240, ArrangementRole.Utility), new("spotify", 0, 320, 240, ArrangementRole.Companion)];
        long Area(ArrangementSlot s) => (long)s.Bounds.Width * s.Bounds.Height;
        void Valid(ArrangementPlan plan, PixelRect work, IReadOnlyList<ArrangementItem> items)
        {
            Assert(plan.Success, "smart plan exists: " + plan.Error);
            Assert(plan.Slots.Count == items.Count, "every exact input identity assigned once");
            foreach (var slot in plan.Slots)
            {
                var r = slot.Bounds; var item = items.Single(i => i.Id == slot.Id);
                Assert(r.Left >= work.Left && r.Top >= work.Top && r.Right <= work.Right && r.Bottom <= work.Bottom, "smart bounds stay in work area");
                Assert(r.Width >= item.MinimumWidth && r.Height >= item.MinimumHeight, "smart minimum preserved");
                Assert(item.AspectRatio == 0 || Math.Abs(r.Height - r.Width / item.AspectRatio) <= 1, "smart locked aspect preserved");
                Assert(plan.Slots.Where(s => s.Id != slot.Id).All(s => r.Right <= s.Bounds.Left || r.Left >= s.Bounds.Right || r.Bottom <= s.Bounds.Top || r.Top >= s.Bounds.Bottom), "smart slots disjoint");
            }
        }
        Assert(SmartArrangement.Recognize(@"exe:C:\Apps\Blender.exe") == ArrangementRole.Workspace && SmartArrangement.Recognize("exe:C:/Music/Spotify.exe") == ArrangementRole.Companion, "catalog uses executable identity");
        Assert(SmartArrangement.Recognize(null) == ArrangementRole.Normal && SmartArrangement.Recognize("exe:C:/Blender/unknown.exe") == ArrangementRole.Normal, "unknown identity never guessed compact/workspace");
        foreach (var work in new[] { new PixelRect(0, 0, 3840, 2080), new(-7680, -100, 0, 4220), new(0, 0, 2160, 3800), new(100, 20, 5220, 1460) })
        foreach (uint dpi in new uint[] { 96, 144, 192 })
        foreach (var input in new[] { named, named.Reverse().ToArray(), new[] { named[3], named[1], named[0], named[2] } })
        {
            var plan = SmartArrangement.Calculate(work, input, 8, dpi); Valid(plan, work, input);
            Assert(Area(plan.Slots.Single(s => s.Id == "blender")) > Area(plan.Slots.Single(s => s.Id == "spotify")) * 2, "creative workspace receives materially more area than companion regardless of catalog order");
            Assert(plan.Slots.SequenceEqual(SmartArrangement.Calculate(work, input, 8, dpi).Slots), "smart deterministic");
        }
        var screen = new PixelRect(0, 0, 3840, 2080);
        var alternatives = SmartArrangement.Candidates(screen, named, 8, 96);
        Assert(alternatives.Count > 1, "reroll exposes multiple feasible candidates");
        Assert(alternatives.Select(p => string.Join(";", p.Slots.OrderBy(s => s.Id).Select(s => s.ToString()))).Distinct().Count() == alternatives.Count, "reroll candidates have distinct identity-to-frame assignments");
        foreach (var alternative in alternatives) Valid(alternative, screen, named);
        var swap = SmartArrangement.Swap(alternatives[0], named, "blender", "chrome"); Valid(swap, screen, named);
        Assert(swap.Slots.Single(s => s.Id == "blender").Bounds == alternatives[0].Slots.Single(s => s.Id == "chrome").Bounds, "swap transfers entire frame rather than resizing around a new center");
        var strict = named.Select(i => i.Id == "blender" ? i with { MinimumWidth = 32768 } : i).ToArray();
        Assert(!SmartArrangement.Swap(alternatives[0], strict, "blender", "chrome").Success, "swap minimum conflict rejected");
        var locked = named.Select(i => i.Id == "blender" ? i with { AspectRatio = 1 } : i).ToArray();
        Assert(!SmartArrangement.Swap(alternatives[0], locked, "blender", "chrome").Success, "swap ratio conflict rejected without distortion");
        Assert(!SmartArrangement.Swap(alternatives[0], named.Select(i => i.Id == "blender" ? i with { Fixed = Region.Full } : i).ToArray(), "blender", "chrome").Success, "saved places are not silently overwritten by swap");
        var eightK = SmartArrangement.Calculate(new(0, 0, 7680, 4320), named, 8, 96);
        Assert(Area(eightK.Slots.Single(s => s.Id == "spotify")) < 7680L * 4320 * .06, "8K100% companion geometry stays compact independently of interface scaling");
        Assert(eightK.Slots.Sum(Area) > 7680L * 4320 * .95, "8K100% uses remaining workspace instead of wasting area to make companion compact");
        var promoted = named.Select(i => i with { Main = i.Id == "spotify" }).ToArray();
        var promotedPlan = SmartArrangement.Calculate(screen, promoted, 8, 96); Valid(promotedPlan, screen, promoted);
        Assert(Area(promotedPlan.Slots.Single(s => s.Id == "spotify")) > Area(promotedPlan.Slots.Single(s => s.Id == "blender")), "make main overrides role guess");
        Assert(SmartArrangement.MainId(named, "spotify") == "blender", "pre-panel utility focus does not demote workspace");
        Assert(SmartArrangement.MainId([new("a", 0, 1, 1), new("b", 0, 1, 1)], "b") == "b", "unknown apps use captured target");
        var equal = SmartArrangement.Calculate(screen, named, 0, 96, equal: true); Valid(equal, screen, named);
        Assert(equal.Slots.Max(Area) - equal.Slots.Min(Area) <= 3840, "equal layout ignores catalog weight hierarchy");
        var parent = new Region(0, 0, 1d / 6, 1d / 6);
        var child = SmartArrangement.InArea(parent, new(0, 0, .5, 1));
        var child2 = SmartArrangement.InArea(parent, new(.5, 0, 1, 1));
        var large = new PixelRect(-7680, 0, 0, 4320);
        var fixedItems = new[] { named[0], named[1], named[2] with { Fixed = child2 }, named[3] with { Fixed = child } };
        var fixedPlan = SmartArrangement.Calculate(large, fixedItems, 8, 96, [parent]); Valid(fixedPlan, large, fixedItems);
        Assert(fixedPlan.Slots.Single(s => s.Id == "spotify").Bounds == PlacementMath.Calculate(large, child, 8, 96), "nested slot maps directly to physical bounds once");
        foreach (var slot in fixedPlan.Slots.Where(s => s.Id is "blender" or "chrome"))
            Assert(slot.Bounds.Left >= -6400 || slot.Bounds.Top >= 720, "automatic windows cannot consume empty or occupied subgrid cells");
        var absentPlan = SmartArrangement.Calculate(large, named[..2], 8, 96, [parent]); Valid(absentPlan, large, named[..2]);
        Assert(absentPlan.Slots.All(s => s.Bounds.Left >= -6400 || s.Bounds.Top >= 720), "empty subgrid remains reserved when apps are absent");
        Assert(!SmartArrangement.Calculate(screen, [named[0] with { Fixed = parent }, named[1] with { Fixed = parent }], 8, 96).Success, "overlapping saved places reject");
        Assert(!SmartArrangement.Calculate(screen, [named[0] with { Fixed = parent, MinimumWidth = 1000 }], 8, 96).Success, "fixed place cannot grow to satisfy minimum");
        Assert(!SmartArrangement.Calculate(screen, [named[0] with { Fixed = parent, AspectRatio = 1 }], 8, 96).Success, "fixed place cannot silently distort locked ratio");
        Assert(!SmartArrangement.Calculate(new(0, 0, 1920, 1040), [new("main", 0, 1280, 720), new("a", 0, 900, 600), new("b", 0, 900, 600), new("c", 0, 900, 600)], 0, 96).Success, "proven small-screen impossibility rejected");
        foreach (int count in new[] { 1, 8, 16, 32 })
        {
            var many = Enumerable.Range(1, count).Select(i => new ArrangementItem(i.ToString(), i % 3 == 0 ? 16d / 9 : 0, 120, 80)).ToArray();
            Valid(SmartArrangement.Calculate(large, many, 8, 192), large, many);
        }
        var old = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"Version\":1,\"ArrangementPreferences\":[{\"AppId\":\"exe:old\",\"AspectRatio\":1.7777777777777777,\"MinimumWidth\":640}]}")!;
        Assert(old.Validate() is null && old.ArrangementPreferences[0].AspectRatio == 16d / 9 && old.ArrangementPreferences[0].Places.Count == 0, "older preferences migrate without losing ratio locks");
        var area = new ArrangementArea { Monitor = "display", Region = parent };
        old.ArrangementAreas.Add(area); old.ArrangementPreferences[0].Places.Add(new() { Monitor = "display", Region = child, AreaId = area.Id });
        Assert(old.Clone().Validate() is null && old.Clone().ArrangementPreferences[0].Places[0].Region == child, "subgrid and saved app slot survive settings roundtrip");
        old.ArrangementPreferences[0].Places[0].Region = Region.Full;
        Assert(old.Validate() is not null, "saved child cannot escape parent area");
        old.ArrangementPreferences[0].Places[0].Region = child;
        old.ArrangementPreferences.Add(new() { AppId = "exe:other", Places = [new() { Monitor = "display", Region = child, AreaId = area.Id }] });
        Assert(old.Validate() is not null, "two persisted app identities cannot claim one slot");
        old.ArrangementPreferences.RemoveAt(1);
        old.ArrangementAreas.Add(new() { Monitor = "display", Region = parent });
        Assert(old.Validate() is not null, "persisted overlapping parent areas rejected");
        old.ArrangementAreas.RemoveAt(1);
        old.ArrangementUiScale = double.NaN; Assert(old.Validate() is not null, "nonfinite interface scale rejected");
        old.ArrangementUiScale = 0; Assert(old.Validate() is null, "automatic interface scale supported");
        var offOriginParent = new Region(1d / 6, 2d / 6, 5d / 6, 1);
        Assert(SmartArrangement.InArea(offOriginParent, Region.Full) == offOriginParent, "full local region preserves exact parent endpoints without floating-point drift");
        Throws(() => SmartArrangement.InArea(Region.Full, new(-.1, 0, 1, 1)), "invalid child coordinates rejected");
    }
}
