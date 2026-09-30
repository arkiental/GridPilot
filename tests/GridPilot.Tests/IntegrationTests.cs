using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using GridPilot.Core;
using GridPilot.UI;
using GridPilot.Windows;

namespace GridPilot.Tests;

internal static partial class Program
{
    private static AppSettings IntegrationSettings() => new()
    {
        Scenes = [new() { Name = "Review", Places = [new() { Label = "Editor", AppId = "exe:editor", Monitor = "display", Region = new(0, 0, .6, 1) }] }],
        DrawPlacement = new(3, 0x44),
        DrawSnapping = new() { Enabled = true, Size = 48, Toggle = new(4, 0x53) },
        PartialWorkspace = new() { Region = new(0, .5, 1, 1), Columns = 3, Rows = 2, Shortcut = new(3, 0x46) },
        Presets = [new() { Name = "Writing desk", Region = new(0, 0, .5, 1), Shortcut = new(3, 0x57), Monitor = "next" }]
    };

    private static void IntegrationSettingsTests()
    {
        var initial = IntegrationSettings();
        Assert(initial.Validate() is null, "workspace and drawing settings coexist without shortcut conflicts");
        var clone = initial.Clone();
        Assert(JsonSerializer.Serialize(clone) == JsonSerializer.Serialize(initial), "clone retains scenes, drawing, partial workspace and named presets together");
        clone.Scenes[0].Name = "Changed"; clone.DrawSnapping.Enabled = false; clone.Presets[0].Name = "Changed";
        Assert(initial.Scenes[0].Name == "Review" && initial.DrawSnapping.Enabled && initial.Presets[0].Name == "Writing desk", "combined drafts do not alias persisted settings");
        var legacy = JsonNode.Parse(JsonSerializer.Serialize(initial))!.AsObject();
        legacy.Remove("DrawPlacement"); legacy.Remove("DrawSnapping");
        legacy["Presets"]![0]!["Shortcut"] = JsonSerializer.SerializeToNode(new Shortcut(3, 0x44));
        var upgraded = JsonSerializer.Deserialize<AppSettings>(legacy.ToJsonString())!;
        Assert(upgraded.DrawPlacement is null && upgraded.Validate() is null, "legacy Ctrl+Alt+D preset is not replaced by a new draw shortcut");
        Assert(upgraded.Scenes[0].Places[0].Region == initial.Scenes[0].Places[0].Region && upgraded.PartialWorkspace.Shortcut == initial.PartialWorkspace.Shortcut,
            "legacy migration retains scenes and configured workspace shortcut");
        string temporary = Path.Combine(Path.GetTempPath(), "GridPilot-integration-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(temporary); store.Save(initial);
            var loaded = store.Load();
            Assert(loaded.Warning is null && JsonSerializer.Serialize(loaded.Settings) == JsonSerializer.Serialize(initial), "atomic persistence retains the combined settings schema");
            var updated = loaded.Settings.Clone(); updated.DrawSnapping.Enabled = false; store.Save(updated);
            Assert(store.Load().Settings.Scenes[0].Id == initial.Scenes[0].Id && store.Load().Settings.Presets[0].Id == initial.Presets[0].Id,
                "remembered snapping updates preserve scene and preset identities");
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }

    private static async Task IntegrationNativeTests()
    {
        Theme.Apply("Dark");
        var initial = IntegrationSettings(); AppSettings? saved = null;
        var settingsEditor = new SettingsWindow(initial, proposed => { saved = proposed.Clone(); return null; }, null) { ShowActivated = false };
        try
        {
            settingsEditor.Show(); settingsEditor.UpdateLayout();
            EditorField<TextBox>(settingsEditor, "snapSize").Text = "64";
            EditorField<ShortcutBox>(settingsEditor, "drawPlacement").Record(new(3, 0x47));
            Click(settingsEditor, "Save settings");
            Assert(saved?.DrawPlacement == new Shortcut(3, 0x47) && saved.DrawSnapping.Size == 64, "real settings editor saves drawing changes");
            Assert(saved is not null && saved.Scenes[0].Id == initial.Scenes[0].Id && saved.PartialWorkspace.Shortcut == initial.PartialWorkspace.Shortcut &&
                saved.Presets[0].Id == initial.Presets[0].Id && saved.Presets[0].Region == initial.Presets[0].Region, "drawing settings edits preserve all workspace features");
        }
        finally { settingsEditor.Close(); }

        using var fixture = StartFixture("--pair");
        try
        {
            var hwnd = await WaitForFixture(fixture); await Task.Delay(150);
            var entries = PreviewWindows.Capture().Where(e => e.Target.ProcessId == fixture.Id).ToArray();
            Assert(entries.Length == 2, "integration owns two fixture windows");
            var target = entries.Single(e => e.Target.Handle == hwnd).Target;
            var neighbor = entries.Single(e => e.Target.Handle != hwnd);
            var monitor = MonitorService.ForWindow(hwnd, MonitorService.GetAll());
            var original = WindowService.VisibleBounds(hwnd);
            var requested = original with { Right = original.Right - 1 };
            var touching = neighbor with { Bounds = new(requested.Right, requested.Top, requested.Right + 100, requested.Bottom) };
            var service = new WindowService(() => [touching]);
            var result = await service.PlaceDrawnAsync(target, monitor, requested, true);
            Assert(!result.Success && WindowService.VisibleBounds(hwnd).Near(original, 0), "Ctrl drawing rejects a native one-pixel frame overrun into an edge-touching neighbor");

            bool failCapture = false;
            service = new WindowService(() => failCapture ? throw new InvalidOperationException("Occupied frames could not be read.") : []);
            var before = entries.Select(e => (e.Target, e.Bounds)).ToArray();
            var firstBounds = SceneGeometry.Restore(new(.05, .05, .45, .8), monitor.Work);
            var secondBounds = SceneGeometry.Restore(new(.55, .1, .95, .85), monitor.Work);
            var sceneFrames = new[] { (target, firstBounds), (neighbor.Target, secondBounds) };
            result = await service.ArrangeAsync(monitor, sceneFrames, before, sceneDisplays: [monitor], allowSceneOverlap: true);
            Assert(result.Success && service.CanUndo, "scene placement creates grouped Undo on the same service used by drawing: " + result.Message);
            failCapture = true;
            result = await service.PlaceDrawnAsync(target, monitor, firstBounds, true);
            Assert(!result.Success && result.Message!.Contains("could not be read") && service.CanUndo &&
                WindowService.VisibleBounds(hwnd).Near(firstBounds) && WindowService.VisibleBounds(neighbor.Target.Handle).Near(secondBounds),
                "failed Ctrl occupancy capture moves no frames and retains prior scene Undo");
            Assert((await service.UndoAsync()).Success && before.All(p => WindowService.VisibleBounds(p.Target.Handle).Near(p.Bounds)), "scene Undo remains usable after a rejected drawing operation");
            failCapture = false;
            result = await service.ArrangeAsync(monitor, sceneFrames, before, sceneDisplays: [monitor], allowSceneOverlap: true);
            Assert(result.Success, "a new scene can be applied after recovery");
            var drawn = firstBounds with { Left = firstBounds.Left + 17, Top = firstBounds.Top + 19, Right = firstBounds.Right + 17, Bottom = firstBounds.Bottom + 19 };
            result = await service.PlaceDrawnAsync(target, monitor, drawn, true);
            Assert(result.Success && WindowService.VisibleBounds(hwnd).Near(drawn) && WindowService.VisibleBounds(neighbor.Target.Handle).Near(secondBounds),
                "successful Ctrl drawing after a scene changes only its selected target: " + result.Message);
            Assert((await service.UndoAsync()).Success && WindowService.VisibleBounds(hwnd).Near(firstBounds) &&
                WindowService.VisibleBounds(neighbor.Target.Handle).Near(secondBounds) && !service.CanUndo, "drawing Undo restores only that operation, without replaying the older scene Undo");
            Console.WriteLine("Integration: combined settings, strict Ctrl occupancy, scene/drawing transaction and Undo isolation passed.");
        }
        finally { if (!fixture.HasExited) { fixture.Kill(); await fixture.WaitForExitAsync(); } }
    }
}
