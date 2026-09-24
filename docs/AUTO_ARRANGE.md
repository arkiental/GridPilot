# Automatic arrangement

Open Focus, select a display, then choose **Auto arrange** next to its grid control. Only visible, resizable application windows belonging to that display are offered. Minimized, other-desktop, hung, modal-blocked, and inaccessible elevated windows are excluded. Excluding a window leaves it in place; it is not minimized, hidden, or treated as a reserved obstacle.

The numbered preview updates as you include windows or expand **App rules**. No window moves until **Arrange windows**. Escape cancels. Up to 32 windows can participate.

- Blank aspect: fill its assigned tile. With all flexible windows the tiles cover the work area except the configured gaps.
- Aspect: use `16:9`, `1:1`, or a decimal. This locks the *visible window frame* ratio, not an application's internal canvas or document.
- Grid shape: `3x2` means the physical aspect of three columns by two rows of the current display grid. It overrides the aspect field. It is a proportional shape preference, not a fixed cell address or guaranteed cell span.
- Minimum: physical visible-frame pixels, such as `1280x720`. Default `320x240`. Applications may impose a larger minimum of their own.
- Remember: saves rules by executable path or packaged-app identity, never by changing window title. Windows of the same app need matching remembered rules. Individual inclusion stays per-window for this run; persistent app exclusion applies only when all its windows are excluded. If identity is inaccessible, rules remain session-only.

Locked ratios cannot always fill a rectangular monitor exactly; unused space is shown rather than stretching those windows. The bounded shelf search evaluates row counts in catalog and constraint order, scores usable area and shape, and refuses layouts that violate explicit minimums. It is a predictable heuristic, not an exhaustive packing solver: “no layout found” does not prove mathematical impossibility. Exclude some windows, relax rules, or use a larger display. It does not distribute one batch across several displays; invoke it once per display.

Application minimums are validated during application, not guessed from frame sizes. A refusing/stale target or display-topology change stops the batch. Previously touched windows are restored with the same verified window-placement restoration used by ordinary Undo. If restoration fails, remaining snapshots stay available to retry Undo. Successful batches replace the previous Undo entry with one group; closed windows are skipped during Undo.

## Research and decisions

1. [Microsoft FancyZones](https://learn.microsoft.com/en-us/windows/powertoys/fancyzones) demonstrates the value of monitor-specific layouts, gaps and exclusions. Here the plan is generated from the current selection instead of choosing permanent zones. Scope stays one display so scale and placement remain explicit.
2. [WM_GETMINMAXINFO](https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-getminmaxinfo) allows applications to override tracking limits. A preferred rectangle is therefore not evidence that an app accepted it. GridPilot compares actual visible bounds after settling and rejects an incompatible batch instead of enlarging a tile into its neighbor.
3. [SetWindowPos](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos) documents asynchronous positioning and flags that preserve activation/Z order. Batch application keeps those flags and verifies each result. Sequential application makes failure attribution and rollback clear; it does not promise visually simultaneous movement.
4. [High DPI desktop development](https://learn.microsoft.com/en-us/windows/win32/hidpi/high-dpi-desktop-application-development-on-windows) explains that coordinates depend on DPI-awareness context. Plans remain physical pixels, gaps convert using destination DPI, and existing per-monitor-aware native scopes perform placement.
5. Exact aspect, minimum dimensions, arbitrary window counts and complete rectangular coverage can conflict. The design therefore makes minimums and ratio preservation hard constraints, coverage a score, and remaining space visible. It never silently relaxes a user's rule.

Remaining hardware checks: mixed-DPI monitor changes during a batch, RDP, protected/packaged applications and real production apps with dynamic minimum sizes. Hosted CI cannot certify those behaviors.
