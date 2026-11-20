# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

CSV Analyser: a Windows desktop app (WPF, .NET 10) for plotting large measurement CSV files (hundreds of MB, ~150 columns), with oscilloscope-like tools. The UI and user-facing strings are in French. The previous WinForms/ZedGraph version was removed from the tree (still in git history, commit `bbfbc2e`).

Planned roadmap: V1 open/plot/cursor/themes (done) → V1.1 zoom undo/redo + Paramètres window (UI size, plot text size, theme) (done) → V2a per-curve settings: name, unit, color, line, markers (done, version 2.0) → V2b multiple Y axes (auto/manual/zero-aligned), stacked plots with drag-and-drop → V3 save/reload configuration (JSON "recipes" keyed by column name, reapplicable to an equivalent file) + CSV export → V4 two-cursor measurements (mean/min/max/delta/RMS) → V5 computed curves (formulas, filters, FFT).

## Commands

```powershell
dotnet build CsvAnalyser.sln
dotnet test CsvAnalyser.sln
dotnet test tests/CsvAnalyser.Tests --filter "FullyQualifiedName~FrenchDateColumn"   # single test
dotnet run --project src/CsvAnalyser.App -- "chemin\fichier.csv"                   # open a file at startup
```

Versioning is automatic (Nerdbank.GitVersioning, enabled for every project by `Directory.Build.props`): the version is `major.minor` from `version.json` plus the number of commits since that line last changed (e.g. `1.0.7`). To start a new version, edit `"version"` in `version.json` (e.g. `"1.1"` or `"2.0"`) and commit. Builds from a branch other than `main` get a `-g<commit>` suffix. The app shows it through `AppInfo.Version` (generated `ThisAssembly` class). A shallow clone breaks the commit count, so CI must fetch full history.

The app icon is drawn in code by `tools/makeicon.cs` (same design as `src/CsvAnalyser.App/Assets/app.svg`, simplified below 40 px). Regenerate with `dotnet run tools/makeicon.cs -- <dossier>` then copy `app.ico` and `app-64.png` into `src/CsvAnalyser.App/Assets/`.

`données de test/` holds a real sample file; `CsvImporterTests.RealSampleFile_IfPresent` loads it when present.

## Architecture

- **`src/CsvAnalyser.Core`** (no UI dependency): CSV import and data model.
  - `CsvFormatDetector` guesses encoding (BOM / UTF-8 / Windows-1252), delimiter (scored by field-count consistency × numeric ratio, so `1,5;2,3` is detected as `;` + decimal `,`), decimal separator, header line after an optional preamble, an optional units row, `[unit]` in headers, and per-column kind (Numeric / Date / Text / Empty).
  - `CsvImporter` reads batches of lines and parses each batch with `Parallel.For` while the next batch is being read, writing straight into one `double[]` per column (~1 s for 400 MB). Invalid, empty and infinite values become `NaN`. Text columns are dropped and listed in `Dataset.IgnoredColumns`.
  - `TimeDetector` picks the default X axis and converts date/epoch columns **in place** to seconds relative to the first sample (`TimeInfo.Origin` keeps the absolute start).
  - `Channel.SourceName` (raw header) is the stable key for future configuration mapping; `DisplayName`/`Unit` are editable.
- **`src/CsvAnalyser.App`** (WPF + WPF-UI Fluent controls + ScottPlot 5 + CommunityToolkit.Mvvm):
  - `MainViewModel` owns the dataset, curve list, X axis choice and theme, and raises `PlotChanged` events.
  - `ChannelItemViewModel` is one curve: plotted flag plus its appearance (color with `IsColorCustom`, `CurveLine`, width, `CurveMarker`, size); `Name`/`Unit` write through to `Channel.DisplayName`/`Unit`. It reports changes through `IChannelHost` (implemented by `MainViewModel`), which raises `PlotChange.Appearance`: the controller updates the plottables in place, so the view is kept. Theme changes recolor only curves without a custom color. `CurveSettingsWindow` (pencil on hover or right-click in the list) binds directly to the item.
  - `PlotController` is the only code that touches ScottPlot: it rebuilds the plottables on each change, draws the snapping vertical cursor and markers, and does its own NaN-aware autoscale (Y fits the visible X range). It also keeps the zoom history (`ViewHistory`): the axis limits before a mouse gesture, or before a burst of wheel ticks (400 ms timer), are pushed when the gesture ends; Ajuster is recorded too; changing file or X axis clears it.
  - `SettingsWindow` binds directly to `MainViewModel` (changes apply live). `UiScale` is a `LayoutTransform` on the toolbar, side panel and status bar (not on the plot: a scaled WpfPlot would be blurry); `PlotFontScale` multiplies the ScottPlot font sizes. Both are saved in `AppSettings` (`%APPDATA%\CsvAnalyser\settings.json`).
  - `PlotThemes` defines the plot themes; each one also selects the WPF-UI light or dark app theme.

## Gotchas

- ScottPlot renders in physical pixels: font sizes and line widths are multiplied by the DPI scale in `PlotController`, and mouse positions are multiplied by `DisplayScale`.
- SignalXY requires ascending X; non-monotonic X columns fall back to scatter points (always drawn as points, line settings ignored).
- The whole curve-list row is a CheckBox, so a double-click toggles the curve twice: open curve settings with the pencil or the context menu, not with double-click.
- Avoid semi-transparent text brushes (e.g. WPF-UI `TextFillColorSecondaryBrush`): on this setup they render far too light. Use the opaque `AppSecondaryTextBrush` set in `MainWindow.ApplyAppTheme`. Mica backdrop is disabled for the same reason. The same applies to the WPF-UI disabled look (a disabled icon button is invisible, and `Opacity` does not help): the undo/redo buttons stay enabled and the `HistoryButton` style grays them with the opaque `AppDisabledTextBrush` through `Tag`. Light grays also render paler than their value, so keep them fairly dark.
- The `NU1701` warning about SkiaSharp.Views.WPF (pulled in by ScottPlot.WPF) is expected and harmless.
