# UI testing notes (cloud environment)

## What was tested

Because WindSonic is a **native WPF desktop app** (`net8.0-windows`), browser-driven tools (Playwright/Puppeteer) cannot directly drive the UI surface. In this Linux cloud environment, running the app window itself is also not possible.

To still test UI reliability, I added and ran a static UI contract audit:

- Validates that `MainWindow.xaml` bindings targeting `MainWindowViewModel` have matching public members.
- Validates that event handlers referenced in XAML exist in `MainWindow.xaml.cs`.

## Results

Current audit result:

- PASS: no missing view-model binding targets in `MainWindow.xaml`
- PASS: no missing code-behind event handlers referenced by XAML

## Recommendation for deeper UI bug coverage

For end-to-end desktop UI regression testing, use a Windows runner and a WPF-capable UI automation tool:

1. **FlaUI** (UIA3) for robust Windows desktop automation.
2. Optional CI setup on GitHub Actions `windows-latest` that launches `WindSonic.exe` and runs smoke tests (search input focus, playback controls, queue interactions, playlist import entry flow).

This can coexist with the static audit script for quick local checks.
