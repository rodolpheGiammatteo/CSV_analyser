# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

CSV Analyser: a Windows desktop app (WPF, .NET 10) for plotting large measurement CSV files (hundreds of MB, ~150 columns), with oscilloscope-like tools. The UI and user-facing strings are in French. The previous WinForms/ZedGraph version was removed from the tree (still in git history, commit `bbfbc2e`).

Planned roadmap: V1 open/plot/cursor/themes (done) → V2 per-curve styling, multiple Y axes (auto/manual/zero-aligned), stacked plots → V3 save/reload configuration (JSON "recipes" keyed by column name, reapplicable to an equivalent file) + CSV export → V4 two-cursor measurements (mean/min/max/delta/RMS) → V5 computed curves (formulas, filters, FFT).

## Commands

```powershell
dotnet build CsvAnalyser.sln
dotnet test CsvAnalyser.sln
dotnet test tests/CsvAnalyser.Tests --filter "FullyQualifiedName~FrenchDateColumn"   # single test
dotnet run --project src/CsvAnalyser.App -- "chemin\fichier.csv"                   # open a file at startup
```

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
  - `PlotController` is the only code that touches ScottPlot: it rebuilds the plottables on each change, draws the snapping vertical cursor and markers, and does its own NaN-aware autoscale (Y fits the visible X range).
  - `PlotThemes` defines the plot themes; each one also selects the WPF-UI light or dark app theme.

## Gotchas

- ScottPlot renders in physical pixels: font sizes and line widths are multiplied by the DPI scale in `PlotController`, and mouse positions are multiplied by `DisplayScale`.
- SignalXY requires ascending X; non-monotonic X columns fall back to scatter points.
- Avoid semi-transparent text brushes (e.g. WPF-UI `TextFillColorSecondaryBrush`): on this setup they render far too light. Use the opaque `AppSecondaryTextBrush` set in `MainWindow.ApplyAppTheme`. Mica backdrop is disabled for the same reason.
- The `NU1701` warning about SkiaSharp.Views.WPF (pulled in by ScottPlot.WPF) is expected and harmless.
