# Node icons

Drop a PNG here named after the node **kind**, and the palette card + Info panel pick it up
automatically (no code change). Missing icon → the colored initial-tile placeholder is used.

- **Filename** = `<kind>.png`, e.g.
  - `LazyFrame.filter.png`
  - `source.csv.png`
  - `Expr.sum.png`
  - `composite.top_rows.png`  (user-generated nodes work too)
- **Size**: rendered at 30×30. Ship at **60×60** (2×) for crispness. Transparent background.
- Referenced at runtime as `avares://Nota/Assets/icons/<kind>.png` via `AssetLoader`
  (see `LoadIcon` / `BuildNodeIcon` in `MainWindow.axaml.cs`).

The kind of any node is shown under its label in the Info panel (click a palette card),
so that's the exact string to use for the filename.

SVG isn't loaded natively — either export to PNG, or add the `Avalonia.Svg.Skia` package
and change `LoadIcon` to build an `SvgImage` instead of a `Bitmap`.
