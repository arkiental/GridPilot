using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;
using GridPilot.Core;
using GridPilot.UI;
using GridPilot.Windows;

namespace GridPilot.Tests;

internal static partial class Program
{
    private static void SceneTests()
    {
        var work = new PixelRect(-1920, -300, 0, 780);
        var frame = new PixelRect(-1800, -200, -800, 400);
        var region = SceneGeometry.Capture(frame, work);
        Assert(SceneGeometry.Restore(region, work).Near(frame, 0), "scene geometry retains physical bounds at negative origin");
        var destination = new PixelRect(0, 0, 3840, 2160);
        var mapped = SceneGeometry.Restore(region, destination);
        Assert(mapped.Width == frame.Width * 2 && mapped.Height == frame.Height * 2, "scene geometry scales to remapped display");
        Assert(SceneGeometry.Capture(new(-2500, -900, 900, 1100), work) == Region.Full, "scene capture fits oversized edge-crossing window into work area");
        var scene = new WorkspaceScene { Name = "Create", Places = [new() { Label = "Editor", AppId = "exe:editor", Monitor = "display", Region = region }] };
        var settings = new AppSettings { Scenes = [scene] };
        Assert(settings.Validate() is null && settings.Clone().Scenes[0].Places[0].Region == region, "scene settings roundtrip preserves normalized geometry");
        var json = JsonSerializer.Serialize(settings);
        Assert(!json.Contains("ProcessId") && !json.Contains("Handle"), "scene persistence never stores HWND or PID");
        Assert(JsonSerializer.Deserialize<AppSettings>("{}")!.Scenes.Count == 0, "old settings acquire empty scenes");
        settings.Scenes.Add(scene); Assert(settings.Validate() is not null, "duplicate scene identities rejected"); settings.Scenes.RemoveAt(1);
        scene.Places[0].Region = new(0, 0, 2, 1); Assert(settings.Validate() is not null, "off-display persisted scene rejected");
        scene.Places[0].Region = region; scene.Places = null!; Assert(settings.Validate() is not null, "null scene places rejected");
        settings.Scenes = null!; Assert(settings.Validate() is not null, "null scene collection rejected");
    }

    private static async Task SceneNativeTests()
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
            Assert(entries.Count == 2, "scene fixture exposes two same-app windows");
            var monitors = MonitorService.GetAll(); var monitor = MonitorService.ForWindow(entries[0].Target.Handle, monitors);
            var before = entries.Select(e => (e.Target, e.Bounds)).ToArray();
            var service = new WindowService(() => PreviewWindows.Capture().Where(e => e.Target.ProcessId == fixture.Id).ToArray());
            var destination = monitors.Count > 1 ? monitors.First(m => m.Device != monitor.Device) : monitor;
            var firstBounds = SceneGeometry.Restore(new(.05, .05, .65, .75), monitor.Work);
            var secondBounds = SceneGeometry.Restore(new(.25, .2, .9, .95), destination.Work);
            var result = await service.ArrangeAsync(monitor, [(entries[0].Target, firstBounds), (entries[1].Target, secondBounds)], before, sceneDisplays: monitors, allowSceneOverlap: true);
            Assert(result.Success && service.CanUndo, "scene transaction accepts explicit overlapping or multi-display geometry: " + result.Message);
            Assert((await service.UndoAsync()).Success && before.All(p => WindowService.VisibleBounds(p.Target.Handle).Near(p.Bounds)), "one scene Undo restores every source frame");
            result = await service.ArrangeAsync(monitor, [(entries[0].Target, firstBounds), (entries[1].Target, new(destination.Work.Left + 10, destination.Work.Top + 10, destination.Work.Left + 11, destination.Work.Top + 11))], before, sceneDisplays: monitors, allowSceneOverlap: true);
            Assert(!result.Success && before.All(p => WindowService.VisibleBounds(p.Target.Handle).Near(p.Bounds)), "scene minimum refusal rolls entire transaction back");
            var changedDisplay = monitors.Select(m => m with { Dpi = m.Dpi + 1 }).ToArray();
            result = await service.ArrangeAsync(monitor, [(entries[0].Target, firstBounds)], before, sceneDisplays: changedDisplay, allowSceneOverlap: true);
            Assert(!result.Success && before.All(p => WindowService.VisibleBounds(p.Target.Handle).Near(p.Bounds)), "stale scene display rejects before movement");

            static T Field<T>(object owner, string field) => (T)owner.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
            var scene = new WorkspaceScene { Name = "Review", Places = entries.Select(e => new ScenePlace { Label = e.Target.Title, AppId = Windows.ApplicationIdentity.Read(e.Target), Monitor = monitor.Device, Region = SceneGeometry.Capture(e.Bounds, monitor.Work) }).ToList() };
            Theme.Apply("Dark");
            IReadOnlyList<WorkspaceScene>? saved = null;
            var sheet = new ScenesWindow(new() { Scenes = [scene] }, proposed => { saved = proposed.ToArray(); return null; }, (_, _, _) => Task.FromResult(new PlacementResult(true)), () => Task.FromResult(new PlacementResult(true)), () => entries);
            var rows = Field<List<(ScenePlace Place, ComboBox Window, ComboBox Monitor)>>(sheet, "rows");
            Assert(rows.Count == 2 && rows.All(r => r.Window.SelectedIndex == 0), "same-app windows never automatically assigned on scene load");
            rows[0].Window.SelectedIndex = 1; rows[1].Window.SelectedIndex = 1;
            var preview = sheet.GetType().GetMethod("BuildPreview", BindingFlags.NonPublic | BindingFlags.Instance)!;
            await (Task)preview.Invoke(sheet, null)!;
            Assert(!Field<Button>(sheet, "applyButton").IsEnabled && Field<TextBlock>(sheet, "status").Text.Contains("once"), "duplicate exact-window assignment rejected");
            rows[1].Window.SelectedIndex = 2;
            await (Task)preview.Invoke(sheet, null)!;
            Assert(Field<Button>(sheet, "applyButton").IsEnabled, "explicit distinct same-app assignments enable scene preview");
            rows[1].Monitor.SelectedItem = null;
            Assert(!Field<Button>(sheet, "applyButton").IsEnabled, "monitor remapping invalidates old preview");
            await (Task)preview.Invoke(sheet, null)!;
            Assert(!Field<Button>(sheet, "applyButton").IsEnabled, "missing scene display requires explicit remapping");
            Field<TextBox>(sheet, "name").Text = "Create";
            sheet.GetType().GetMethod("CaptureScene", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(sheet, null);
            Assert(saved?.Last().Name == "Create" && saved.Last().Places.Count == 2, "save captures owned catalog and retains requested name");
            sheet.Close();
            Console.WriteLine($"Scenes: explicit identity mapping, stale preview, persistence, grouped Undo and rollback passed; {monitors.Count} connected display(s).");
        }
        finally { if (!fixture.HasExited) { fixture.Kill(); await fixture.WaitForExitAsync(); } }
    }
}
