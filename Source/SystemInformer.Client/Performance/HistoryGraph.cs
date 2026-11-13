using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using SystemInformer.Client.Themes;

namespace SystemInformer.Client.Performance;

internal sealed class HistoryGraph : Control
{
    private PerformanceSeries? _series;
    private DateTimeOffset _time;
    private int _seconds = 60;
    private bool _compact;
    private HistoryPoint? _hover;
    private ResourceKind _kind;

    public HistoryGraph()
    {
        MinHeight = 40;
        ClipToBounds = true;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
        AttachedToVisualTree += (_, _) => InvalidateVisual();
        PointerMoved += Hover;
        PointerExited += (_, _) => { _hover = null; ToolTip.SetIsOpen(this, false); InvalidateVisual(); };
    }

    public void Update(PerformanceSeries series, ResourceKind kind, DateTimeOffset time, int seconds, bool compact = false)
    {
        _series = series; _time = time; _seconds = seconds; _compact = compact;
        _kind = kind;
        if (_hover != null && !series.Points.Contains(_hover)) { _hover = null; ToolTip.SetIsOpen(this, false); }
        InvalidateVisual();
    }

    // Series distinguish resource categories, never success/failure states.
    private (string First, string Fill, string Second, string SecondFill) Colors(ResourceKind kind) => (ActualThemeVariant == ThemeVariant.Dark, kind) switch
    {
        (true, ResourceKind.Cpu or ResourceKind.Memory) => ("InfoIconBrush", "InfoBorderBrush", "CategoryCodeFgBrush", "CategoryCodeBorderBrush"),
        (true, ResourceKind.Io) => ("CategoryArtFgBrush", "CategoryArtStripeBrush", "CategoryCodeFgBrush", "CategoryCodeBorderBrush"),
        (true, ResourceKind.Network) => ("InfoIconBrush", "InfoBorderBrush", "CategoryArtFgBrush", "CategoryArtStripeBrush"),
        (true, _) => ("CategoryCodeFgBrush", "CategoryCodeBorderBrush", "InfoIconBrush", "InfoBorderBrush"),
        (_, ResourceKind.Cpu or ResourceKind.Memory) => ("InfoIconBrush", "InfoBgBrush", "CategoryCodeStripeBrush", "CategoryCodeBgBrush"),
        (_, ResourceKind.Io) => ("CategoryArtFgBrush", "CategoryArtBgBrush", "CategoryCodeStripeBrush", "CategoryCodeBgBrush"),
        (_, ResourceKind.Network) => ("InfoIconBrush", "InfoBgBrush", "CategoryArtFgBrush", "CategoryArtBgBrush"),
        _ => ("CategoryCodeStripeBrush", "CategoryCodeBgBrush", "InfoIconBrush", "InfoBgBrush")
    };

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var plot = new Rect(0.5, 0.5, Math.Max(0, Bounds.Width - 1), Math.Max(0, Bounds.Height - 1));
        var gridPen = new Pen(MonitorTheme.Brush(this, ActualThemeVariant == ThemeVariant.Dark ? "BorderStrongBrush" : "BorderDefaultBrush"), 1);
        var muted = MonitorTheme.Brush(this, "TextSecondaryBrush");
        context.DrawRectangle(MonitorTheme.Brush(this, "BgSurfaceSubtleBrush"), gridPen, plot);
        if (plot.Width < 2 || plot.Height < 2 || _series == null) return;
        var points = _series.Points.Where(point => point.Time >= _time.AddSeconds(-_seconds) && point.Time <= _time).ToArray();
        var maximum = _series.Maximum is > 0 ? _series.Maximum.Value
            : Math.Max(1, points.Select(point => Math.Max(point.First ?? 0, Top(point, _series.SecondLabel != null ? 1 : 0) ?? 0)).DefaultIfEmpty().Max() * 1.1);
        var palette = Colors(_kind);
        DrawSeries(context, plot, points, maximum, 0, MonitorTheme.Brush(this, palette.First), MonitorTheme.Brush(this, palette.Fill));
        if (_series.SecondLabel != null) DrawSeries(context, plot, points, maximum, 1, MonitorTheme.Brush(this, palette.Second),
            _series.Stacked ? MonitorTheme.Brush(this, palette.SecondFill) : null);
        if (!_compact)
        {
            for (var i = 1; i < 12; i++) context.DrawLine(gridPen, new(plot.X + i * plot.Width / 12, plot.Y), new(plot.X + i * plot.Width / 12, plot.Bottom));
            for (var i = 1; i < 4; i++) context.DrawLine(gridPen, new(plot.X, plot.Y + i * plot.Height / 4), new(plot.Right, plot.Y + i * plot.Height / 4));
            var label = Text(Value(maximum), 11, muted);
            context.DrawText(label, new(Math.Max(4, plot.Right - label.Width - 5), 3));
            if (!points.Any(point => point.First.HasValue))
            {
                var empty = Text("等待采样或数据不可读", 12, muted);
                context.DrawText(empty, new(Math.Max(6, (plot.Width - empty.Width) / 2), Math.Max(5, (plot.Height - empty.Height) / 2)));
            }
        }
        if (_hover != null && !_compact)
        {
            var x = X(_hover.Time, plot);
            context.DrawLine(new Pen(MonitorTheme.Brush(this, "BorderSelectedBrush"), 1), new(x, plot.Y), new(x, plot.Bottom));
        }
    }

    private void DrawSeries(DrawingContext context, Rect plot, HistoryPoint[] points, double maximum, int channel, IBrush stroke, IBrush? fillBrush)
    {
        var segment = new List<HistoryPoint>();
        void Flush()
        {
            if (segment.Count == 0) return;
            var fill = new StreamGeometry();
            using (var path = fill.Open())
            {
                path.BeginFigure(Position(segment[0], channel, false, plot, maximum), true);
                for (var i = 1; i < segment.Count; i++) path.LineTo(Position(segment[i], channel, false, plot, maximum));
                for (var i = segment.Count - 1; i >= 0; i--) path.LineTo(Position(segment[i], channel, true, plot, maximum));
                path.EndFigure(true);
            }
            if (fillBrush != null) context.DrawGeometry(fillBrush, null, fill);
            var line = new StreamGeometry();
            using (var path = line.Open())
            {
                path.BeginFigure(Position(segment[0], channel, false, plot, maximum), false);
                for (var i = 1; i < segment.Count; i++) path.LineTo(Position(segment[i], channel, false, plot, maximum));
                path.EndFigure(false);
            }
            context.DrawGeometry(null, new Pen(stroke, ActualThemeVariant == ThemeVariant.Dark ? (_compact ? 1.5 : 2) : (_compact ? 1 : 1.4)), line);
            segment.Clear();
        }
        foreach (var point in points)
        {
            if (Top(point, channel) == null) { Flush(); continue; }
            if (segment.Count > 0 && point.Time - segment[^1].Time > TimeSpan.FromSeconds(2.5)) Flush();
            segment.Add(point);
        }
        Flush();
    }

    private double? Top(HistoryPoint point, int channel) => channel == 0 ? point.First
        : _series!.Stacked ? point.First.HasValue && point.Second.HasValue ? point.First + point.Second : null : point.Second;
    private Point Position(HistoryPoint point, int channel, bool lower, Rect plot, double maximum)
    {
        var value = lower ? channel == 1 && _series!.Stacked ? point.First ?? 0 : 0 : Top(point, channel) ?? 0;
        return new(X(point.Time, plot), plot.Bottom - Math.Clamp(value / maximum, 0, 1) * plot.Height);
    }
    private double X(DateTimeOffset time, Rect plot) => plot.X + Math.Clamp((_seconds - (_time - time).TotalSeconds) / _seconds, 0, 1) * plot.Width;

    private void Hover(object? sender, PointerEventArgs args)
    {
        if (_compact || _series == null || Bounds.Width <= 1) return;
        var target = _time.AddSeconds(-_seconds * (1 - Math.Clamp(args.GetPosition(this).X / Bounds.Width, 0, 1)));
        var point = _series.Points.Where(point => point.Time >= _time.AddSeconds(-_seconds))
            .MinBy(point => Math.Abs((point.Time - target).TotalSeconds));
        if (point == null || Math.Abs((point.Time - target).TotalSeconds) > 2.5)
        { _hover = null; ToolTip.SetIsOpen(this, false); InvalidateVisual(); return; }
        _hover = point;
        var text = $"{point.Time:yyyy/MM/dd HH:mm:ss}\n{_series.FirstLabel}：{Value(point.First)}";
        if (_series.SecondLabel != null) text += $"\n{_series.SecondLabel}：{Value(point.Second)}";
        if (_series.Stacked && point.First.HasValue && point.Second.HasValue) text += $"\n合计：{Value(point.First + point.Second)}";
        if (point.Note != null) text += "\n" + point.Note;
        ToolTip.SetTip(this, text); ToolTip.SetIsOpen(this, true); InvalidateVisual();
    }

    private string Value(double? value) => _series?.Unit switch
    {
        "%" => ResourceFormat.Percent(value), "B" => ResourceFormat.Bytes(value), "B/s" => ResourceFormat.Rate(value),
        _ => ResourceFormat.Number(value)
    };
    private static FormattedText Text(string text, double size, IBrush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default), size, brush);
}

internal sealed class MemoryComposition : Control
{
    private IReadOnlyList<MemoryPart>? _parts;
    private static readonly string[] Palette = ["InfoBorderBrush", "CategoryCodeStripeBrush", "InfoBgBrush", "BgSurfaceSubtleBrush"];
    private static readonly string[] DarkPalette = ["InfoIconBrush", "CategoryCodeStripeBrush", "InfoBorderBrush", "BgSurfaceSubtleBrush"];
    public MemoryComposition()
    {
        Height = 28;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
        AttachedToVisualTree += (_, _) => InvalidateVisual();
        PointerMoved += (_, args) => ShowPart(args.GetPosition(this).X);
    }
    public void Update(IReadOnlyList<MemoryPart>? parts) { _parts = parts; InvalidateVisual(); }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_parts == null || _parts.Count == 0) return;
        var total = _parts.Sum(part => part.Bytes);
        if (total <= 0) return;
        double x = 0;
        var palette = ActualThemeVariant == ThemeVariant.Dark ? DarkPalette : Palette;
        for (var i = 0; i < _parts.Count; i++)
        {
            var width = Bounds.Width * _parts[i].Bytes / total;
            context.DrawRectangle(MonitorTheme.Brush(this, palette[Math.Min(i, palette.Length - 1)]),
                new Pen(MonitorTheme.Brush(this, "BorderStrongBrush")), new(x, 0.5, width, Bounds.Height - 1));
            x += width;
        }
    }
    private void ShowPart(double x)
    {
        if (_parts == null || Bounds.Width <= 0) return;
        var target = _parts.Sum(part => part.Bytes) * Math.Clamp(x / Bounds.Width, 0, 1);
        double sum = 0;
        foreach (var part in _parts)
        {
            sum += part.Bytes;
            if (target <= sum) { ToolTip.SetTip(this, part.Label + "：" + ResourceFormat.Bytes(part.Bytes)); break; }
        }
    }
}
