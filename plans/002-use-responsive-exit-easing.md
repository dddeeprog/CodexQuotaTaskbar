# 002 — Use responsive exit easing

- **Status**: DONE
- **Commit**: 2850aa6
- **Severity**: MEDIUM
- **Category**: Easing and duration
- **Estimated scope**: 3 files, about 20 lines

## Problem

The details popover and status badge use `EaseIn` for exit, creating a slow start followed by a sudden disappearance.

```csharp
// src/CodexQuotaTaskbar.Host/UI/QuotaPopoverWindow.xaml.cs:97 — current
var easing = new CubicEase { EasingMode = EasingMode.EaseIn };

// src/CodexQuotaTaskbar.Host/UI/QuotaCapsuleWindow.xaml.cs:170 — current
var fade = new DoubleAnimation(1, 0, exitDuration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
```

## Target

Keep the current `130ms` popover and `160ms` badge exit durations, but use `CubicEase(EaseOut)` consistently for opacity, scale, and anchored translation. The transition must respond immediately and settle without bounce.

## Repo conventions to follow

- Entrances already use `CubicEase(EaseOut)`.
- Motion is transform and opacity only and respects `SystemParameters.ClientAreaAnimation`.

## Steps

1. Replace popover exit `EaseIn` with a shared `EaseOut` instance for opacity, scale, and translation.
2. Replace badge and individual status-light exit `EaseIn` with `EaseOut`.
3. Apply the same easing to badge and light scale animations that currently have no easing.
4. Add tests that expose the selected exit easing mode through small pure helpers.

## Boundaries

- Do NOT change colors, geometry, entrance offsets, or durations.
- Do NOT add bounce or springs.
- Do NOT add dependencies.

## Verification

- **Mechanical**: run quota capsule and popover UI tests, then full Release verification.
- **Feel check**: repeatedly open and close details and let each status light reach zero; disappearance starts immediately and does not accelerate at the end.
- **Done when**: every exit channel uses the same responsive curve and remains interrupt-safe through existing generation checks.
