# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

CSV Analyser: a Windows desktop app (WPF, .NET 10) for plotting large measurement CSV files (hundreds of MB, ~150 columns), with oscilloscope-like tools. The UI and user-facing strings are in French. The previous WinForms/ZedGraph version was removed from the tree (still in git history, commit `bbfbc2e`).

Planned roadmap: V1 open/plot/cursor/themes (done) → V1.1 zoom undo/redo + Paramètres window (UI size, plot text size, theme) (done) → V2a per-curve settings: name, unit, color, line, markers (done, version 2.0) → V2b multiple Y axes (auto/manual/zero-aligned), stacked plots with drag-and-drop (done, version 2.1; 2.2 adds graph titles, draggable legends, fit X/Y separately, free axis sharing across units, cursor card grouped by graph and placeable, startup drop zone) → V3 save/reload configuration (JSON "recipes" keyed by column name, reapplicable to an equivalent file) + CSV export → V4 two-cursor measurements (mean/min/max/delta/RMS) → V5 computed curves (formulas, filters, FFT).

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
  - Stacked graphs and Y axes: `MainViewModel.Graphs` (always ≥ 1, empty ones are removed) and `ChannelItemViewModel.Graph`/`OwnAxis`. `GraphLayout.Build` turns them into `GraphSpec`/`AxisSpec`: in a graph, curves of the same unit share an axis (key `unit:<unit>`) unless `ChannelItemViewModel.AxisChoice` says otherwise (`"own"` → key `curve:<source name>`, or any other axis key to share that axis whatever the units; choices from `GraphLayout.AxisChoices`); axes alternate left/right. A graph also has a title (`Name`) and a legend corner + drag offset (`LegendCorner`, `LegendOffset`; the legend is dragged in `PlotController`, anchored to the nearest corner). The cursor card shows `MainViewModel.CursorGroups` (one group per graph) and is moved between three `ContentControl` hosts (left/right/bottom) by `MainWindow.PlaceCursorCard` from `CursorPanelPlacement` (saved in settings). Per-axis auto/manual range lives in `GraphViewModel.GetAxisSettings(key)`, zero alignment in `GraphViewModel.AlignZeros` (math in Core `ZeroAlignment`, unit-tested). Curves are moved by drag-and-drop (list or cursor card → a graph, or the "Nouveau graphe" strip), handled in `MainWindow` code-behind with `PlotController.GraphIndexAt/GraphBounds`.
  - `PlotController` is the only code that touches ScottPlot. One `WpfPlot` in Multiplot mode: one subplot per graph (rows, shared X, X ticks only on the bottom one); a `PaddingMatcher` pre-render action gives all subplots the same left/right padding so data areas line up, and first re-applies zero alignment (`KeepZerosAligned`: the Y axis grabbed at mouse-down/wheel leads, the others shift keeping their span — ScottPlot resets non-grabbed axes on every drag move, so "the axis that changed" is not reliable). `Rebuild` recreates subplots, axes and plottables on structural changes and keeps X plus the Y range of axes whose curves did not change; `UpdateAppearance` updates in place when the layout signature is unchanged. It draws the snapping vertical cursor across all graphs (plus, with `ShowCursorLabels`, a value label per point, spread vertically per graph by `PlaceValueLabels` so they never overlap) and does its own NaN-aware autoscale (`FitGraphY`: manual bounds, else data in the visible X range, then zero alignment). ScottPlot's middle-click autoscale is replaced by ours, its benchmark double-click disabled, its context menu replaced (French, "Réglages des axes…"). Zoom history (`ViewHistory`) stores `ViewSnapshot`s keyed `X` / `graphId|axisKey`, recorded when a mouse gesture or a burst of wheel ticks (400 ms timer) ends, and on Ajuster/axis-setting changes; changing file or X axis clears it.
  - `SettingsWindow` binds directly to `MainViewModel` (changes apply live). `UiScale` is a `LayoutTransform` on the toolbar, side panel and status bar (not on the plot: a scaled WpfPlot would be blurry); `PlotFontScale` multiplies the ScottPlot font sizes. Both are saved in `AppSettings` (`%APPDATA%\CsvAnalyser\settings.json`).
  - `PlotThemes` defines the plot themes; each one also selects the WPF-UI light or dark app theme.

## Gotchas

- ScottPlot renders in physical pixels: font sizes and line widths are multiplied by the DPI scale in `PlotController`, and mouse positions are multiplied by `DisplayScale`.
- SignalXY requires ascending X; non-monotonic X columns fall back to scatter points (always drawn as points, line settings ignored).
- The whole curve-list row is a CheckBox, so a double-click toggles the curve twice: open curve settings with the pencil or the context menu, not with double-click.
- Avoid semi-transparent text brushes (e.g. WPF-UI `TextFillColorSecondaryBrush`): on this setup they render far too light. Use the opaque `AppSecondaryTextBrush` set in `MainWindow.ApplyAppTheme`. Mica backdrop is disabled for the same reason. The same applies to the WPF-UI disabled look (a disabled icon button is invisible, and `Opacity` does not help): the undo/redo buttons stay enabled and the `HistoryButton` style grays them with the opaque `AppDisabledTextBrush` through `Tag`; the min/max boxes of the axes window are never disabled (typing a bound switches the axis to manual), and controls that cannot apply are hidden rather than disabled. Context menus use our own implicit `ContextMenu` style and template in `App.xaml` (`AppMenuBackground`/`AppMenuForeground`); do not base it on the WPF-UI style (that fell back to the classic template, white icon gutter in dark theme). The toolbar must fit a ~1000 px window, or "Paramètres" (docked right) gets pushed off. Light grays also render paler than their value, so keep them fairly dark.
- The `NU1701` warning about SkiaSharp.Views.WPF (pulled in by ScottPlot.WPF) is expected and harmless.
