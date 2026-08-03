# 004 — Use one masked glass capture and one coherent expansion

- **Status**: DONE
- **Commit**: 2850aa6
- **Severity**: HIGH
- **Category**: Materials, performance, and physicality
- **Estimated scope**: 3 files, about 140 lines

## Problem

`SessionStackWindow` attaches `BlurredBackground.WPF` independently to every dynamic card, collapsed layer, and toggle. In the transparent WPF window these captures recursively sample one another: the expanded second card shows a dirty horizontal band and the third card becomes solid black.

```csharp
// src/CodexQuotaTaskbar.Host/UI/SessionStackWindow.xaml.cs:797 — current
foreach (var button in SessionsPanel.Children.OfType<Button>())
{
    ApplySessionButtonAppearance(button, opaque);
}
ConfigureGlass(CollapsedMiddleLayer, !opaque);
ConfigureGlass(CollapsedBackLayer, !opaque);
```

Expansion also combines a `24ms` stagger, `BackEase`, and a fade from `0.28`, so the toggle, cards, shadows, and material arrive as separate events rather than one continuous stack transformation.

```csharp
// src/CodexQuotaTaskbar.Host/UI/SessionStackWindow.xaml.cs:615 — current
private static IEasingFunction CreateExpansionMovementEasing() => new BackEase
{
    Amplitude = ExpansionOvershootAmplitude,
    EasingMode = EasingMode.EaseOut,
};
```

## Target

- Attach `BlurredBackground.WPF` exactly once, to a root `GlassCaptureSurface` behind all cards.
- Give that surface an absolute-coordinate `DrawingBrush` opacity mask containing only the visible rounded card/layer/toggle rectangles. Keep one `TranslateTransform` per masked moving rectangle so blur follows card motion.
- Keep the first card mask and visual completely stationary.
- Move card 2, card 3, their shadows, their mask geometries, and the toggle at the same time for `220ms` using `CubicEase(EaseInOut)`. No stagger, no bounce, and no expansion opacity animation.
- Preserve the symmetric paths: expanded card 2 begins/ends at the middle collapsed layer; card 3 begins/ends at the back layer.
- Continue respecting `SystemParameters.ClientAreaAnimation`; reduced motion changes state immediately.
- High contrast disables the one capture and uses opaque system brushes.

## Repo conventions to follow

- `ExpansionCardOffset(index)` is the existing source of truth for the second and third card paths.
- `ExpansionToggleOffset(totalCount, targetExpanded)` is the existing source of truth for the toggle path.
- `BlurredBackground.WPF` parameters remain `BlurRadius=20`, `Merging=0.92`, `Dpi=48`.
- `ShadowSlots` mirrors `SessionsPanel` indices and must use the same transform as its card.

## Steps

1. In `src/CodexQuotaTaskbar.Host/UI/SessionStackWindow.xaml`, add one root `Border x:Name="GlassCaptureSurface" Grid.RowSpan="2"` before the content grids. It must be non-interactive, stretch to the root, and use a nearly transparent neutral background so the blur library has a valid surface.
2. In `src/CodexQuotaTaskbar.Host/UI/SessionStackWindow.xaml.cs`, remove all per-card, per-layer, ghost, and toggle calls to `ConfigureGlass`. Configure only `GlassCaptureSurface`, still gated until `Loaded` to avoid the library's pre-load null reference.
3. Build `UpdateGlassMask(double maskHeight)` using a `DrawingGroup`/`DrawingBrush` with absolute `Viewbox` and `Viewport` coordinates. Add rounded rectangles for the top card; card 2 and 3 when expanded or the actual one/two collapsed layers when collapsed; and the toggle when session count exceeds one. Clip expanded card masks to the scroll viewport.
4. Store the card/layer mask `TranslateTransform` objects by visual index and the toggle mask transform. On expand, animate indices 1 and 2 from `ExpansionCardOffset(index)` to `0`; on collapse, animate the target layer masks from `-ExpansionCardOffset(index)` to `0`. Animate the toggle mask from `ExpansionToggleOffset(...)` to `0`.
5. Replace expansion movement easing with `CubicEase { EasingMode = EaseInOut }`, set every expansion delay to zero, and remove card/layer opacity animations from expansion/collapse. Do not alter addition, removal, scroll, popover, or badge motion.
6. Extend `tests/CodexQuotaTaskbar.Host.Tests/UI/SessionStackWindowTests.cs` with pure helper assertions: one capture target, zero expansion stagger, no overshoot, exact mask positions for collapsed and expanded states, and symmetric mask offsets.

## Boundaries

- Do NOT remove `BlurredBackground.WPF` from the project or change the working blur used by the quota island and details window.
- Do NOT attach the library to more than one Border in `SessionStackWindow`.
- Do NOT move the first session card, the quota island, or the session stack top.
- Do NOT change card size, gaps, scroll amplitude, shadows, task status logic, or session ordering.
- Do NOT add another dependency.

## Verification

- **Mechanical**: run `dotnet test tests/CodexQuotaTaskbar.Host.Tests/CodexQuotaTaskbar.Host.Tests.csproj -c Release`, then `.\build.ps1 -Target Verify -Configuration Release`.
- **Feel check**: run the packaged app over both a dark Codex window and a light desktop. Expand and collapse a three-session stack via UI Automation and capture the final frames. Confirm all cards show the same neutral translucent material, no card is black, no horizontal capture band appears, the first card never moves, and card 2/card 3/toggle begin and settle together without overshoot.
- **Done when**: one live process remains responsive for at least eight seconds with zero new `.NET Runtime` or `Application Error` events, and the expanded screenshot contains three visually consistent glass cards.
