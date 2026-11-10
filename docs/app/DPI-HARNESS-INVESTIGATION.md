# Screenshot-harness DPI investigation (status: mechanism narrowed, not proven, nothing fixed)

Recorded 2026-11-10 during the A+ 2 work. **This is a test-harness investigation. No harness or product DPI fix has been made or is proposed as part of the visual change.**

## The observation

`LayoutScreenshotTests` renders the compact flyout at a simulated DPI. The same state measured a different content height depending on how the tests were run:

| Code state | `LayoutScreenshotTests` run alone | Whole `QuotaDuck.App.Tests` project |
|---|---|---|
| before A+ 2 (`main`) | 331 DIP (3 of 3 runs) | 315 DIP (every full run seen) |
| A+ 2 | 337 DIP (every run) | 319 DIP **or** 337 DIP (319 in most full runs, 337 in some, with identical code) |

The difference is 16 DIP before A+ 2 and 18 DIP after. The machine's Windows display scale is 125% (registry `AppliedDPI` = 120).

## What is established

1. **Arithmetic fits.** In the "low" state the pond and gauges are built at an integer scale of 2 *at the machine's 125%*, instead of at the harness' requested 100%: pond 64 -> 51.2 DIP tall, two gauges 12 -> 9.6 DIP each, about 17.6 DIP in total. The A+ 2 renders in the low state show a pond that is narrower than the card content, which is that mismatch made visible. The "high" state is the requested 100%.
2. **Run alone is stable.** Running only `LayoutScreenshotTests` gave the same height every time (331 before, 337 after).
3. **Not simply parallelism.** Running the whole project with xUnit test-collection parallelism disabled (`xUnit.ParallelizeTestCollections=false`) still gave 319 in some runs and 337 in others, and so did putting the layout class in its own non-parallel collection. So the variation is not just concurrent tests interfering; it behaves like state left behind by whichever other test classes ran earlier (xUnit does not guarantee a fixed class order).
4. **`SetRootDpi` itself survives a real window.** A throw-away probe (removed, not committed) showed that, after a real `Window` was shown in the same process, `VisualTreeHelper.SetRootDpi` on a root `Border` was still honoured by a child (`GetDpi` = 1.0) while the window itself reported 1.25.
5. **Earlier narrowing (single runs):** the layout class together with `AccessibilityTreeTests` or `PlacementTests` gave the low value; together with `FlyoutWindowTests`, `TrayIconTests`, `ShellTests` or `SetupGuideShellTests` it gave the high value. Given point 3 this is probably ordering luck, not a property of those classes.

## What is NOT established

- The exact mechanism. The most likely area is `PixelImage.Rebuild`, which reads `VisualTreeHelper.GetDpi(this)` when its `Scene`/`TargetWidth` property is set (during binding, possibly before the element is attached to the host that carries the root DPI) and caches bitmaps in a plain static `Dictionary` shared by every test thread. That is a hypothesis, not a finding.
- Whether the same early-DPI read can matter in the real product (an unattached element reads the process/system DPI, which on a mixed-DPI setup may differ from the monitor the flyout opens on). `OnDpiChanged` rebuilds, so it is probably self-correcting, but it has **not** been checked on real mixed-DPI hardware.

## Effect on this PR's evidence

- The before/after screenshots in `docs/app/a-plus-screenshots/` were taken from `LayoutScreenshotTests` run on its own (the stable state).
- Flyout width is now computed from the DPI at measure time. In the harness's low state the card width (at the requested DPI) and the art (at the machine DPI) can disagree; that is why the full-project metrics for the normal state read 319 DIP while the stable value is 337 DIP. No assertion depends on those heights.
- The real-window renders (`R*` in the screenshot test) use a real `Window` and therefore the machine's real DPI; at 125% they show the cards and art matching.

## Candidate corrections (not implemented; each needs approval)

- Test-only: make the harness build its `PixelImage`s only after the host is attached with its root DPI, and clear `PixelImage.Cache` (or key it by test) between renders, so the result does not depend on test order.
- Product: in `PixelImage`, rebuild when the element is loaded / its visual parent changes, so the first DPI read is always the real one.
- Diagnostic first: log `GetDpi` at each `Rebuild` during a failing full run to find the call that reads 1.25.

## Outstanding

- Real-display validation at 100%, 150% and 200% (and the 125% display used here): still manual.
- A mixed-DPI (two monitors at different scales) check of the flyout art scale: still manual.
