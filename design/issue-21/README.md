# GridPilot polish concepts — issue #21

Five visual directions for the existing Focus layout. The owner [approved 02 Smoked Glass with 01 Matte Focus as fallback](https://github.com/arkiental/GridPilot/issues/21#issuecomment-5814058528). This branch now implements that direction; the five images below remain generated concept references, not screenshots of the running app.

All directions retain the large desktop preview, 6 × 6 grid, red selected-window marker, app icon/title, attached two-column window sidebar, monitor selector and five preset controls. Generated lettering and fine screenshot detail are illustrative, not implementation specifications.

| Concept | What changes | Tradeoff |
| --- | --- | --- |
| 01 Matte Focus | Cleaner opaque charcoal, precise borders and restrained orange | Safest readability and simplest rendering |
| 02 Smoked Glass | Subtle dark transparency around opaque preview surfaces | Recommended balance; backdrop rendering needs a prototype |
| 03 Half Transparent | Stronger transparency with solid backing behind important controls | Most experimental; busy wallpaper can reduce readability |
| 04 Graphite Edge | Sharper separators and subtle opaque surface hierarchy | Most utilitarian and predictable |
| 05 Soft Acrylic | Softer frosted appearance and gentler chrome | Attractive, but blur/composition cost must be measured |

**Recommendation: 02 Smoked Glass**, with 01 Matte Focus as the opaque fallback. It explores the requested transparency without weakening the desktop preview. Start any approved implementation with a rendering prototype: keep screenshot and native DWM thumbnail content fully opaque, confirm native thumbnail compatibility, and compare open/drag/scroll performance with the opaque baseline. These illustrations cannot establish performance. Opacity figures in prompts describe visual intent, not verified Windows compositor settings.

## Implementation

The existing layout uses a charcoal-tinted native Desktop Acrylic shell on eligible Windows 11 22H2+ desktops. Desktop screenshots, thumbnail cards and popup surfaces stay opaque; orange accents and the red HWND+PID selection marker retain their existing behavior. Settings offers an opaque Matte Focus switch. System transparency preferences, high contrast, remote sessions, battery saver, insufficient rendering capability and native API failures select the opaque fallback. Light appearance remains opaque.

The implementation follows Microsoft's [system backdrop API](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwm_systembackdrop_type) and [WPF glass interoperation](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/extend-glass-frame-into-a-wpf-application). A normal, nonlayered HWND preserves native DWM thumbnail compatibility. No WPF blur, repeated desktop captures, whole-window opacity or rendering timer is added. The OS controls the material; its appearance can differ from the generated illustration. See [validation notes](../../docs/VALIDATION.md) for tested and remaining scenarios.

## Matte Focus

![Matte Focus](01-matte-focus.png)

## Smoked Glass

![Smoked Glass](02-smoked-glass.png)

## Half Transparent

![Half Transparent](03-half-transparent.png)

## Graphite Edge

![Graphite Edge](04-graphite-edge.png)

## Soft Acrylic

![Soft Acrylic](05-soft-acrylic.png)

## Provenance and validation

- Reference: owner-supplied screenshot in issue #21, saved as [reference.png](reference.png).
- Generated with the built-in imagegen tool, one call per concept; [complete prompts](PROMPTS.md).
- Gallery images reviewed for overall layout, interface palette and selection treatment.
- The original concept-only commit did not change source code. Subsequent implementation validation is recorded separately in [VALIDATION.md](../../docs/VALIDATION.md); raster concepts do not establish performance or rendering correctness.
