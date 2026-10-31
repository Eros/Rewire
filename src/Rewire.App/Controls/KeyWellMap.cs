using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Rewire.Core.Keyboards;
using Rewire.Core.Layouts;
using PhysicalKey = Rewire.Core.Keyboards.PhysicalKey;

namespace Rewire.Controls;

/// <summary>
/// Draws a keyboard to scale from its pack, labelling every key with what it does on one layer
/// of an <see cref="EffectiveLayout"/>. Shared by the configurator and the trainer.
/// </summary>
public sealed class KeyWellMap : Control
{
    public static readonly StyledProperty<EffectiveLayout?> LayoutProperty =
        AvaloniaProperty.Register<KeyWellMap, EffectiveLayout?>(nameof(Layout));

    public static readonly StyledProperty<string> LayerIdProperty =
        AvaloniaProperty.Register<KeyWellMap, string>(nameof(LayerId), "base");

    public static readonly StyledProperty<string?> SelectedKeyIdProperty =
        AvaloniaProperty.Register<KeyWellMap, string?>(nameof(SelectedKeyId), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<string?> HighlightKeyIdProperty =
        AvaloniaProperty.Register<KeyWellMap, string?>(nameof(HighlightKeyId));

    public static readonly StyledProperty<IReadOnlyDictionary<string, double>?> HeatProperty =
        AvaloniaProperty.Register<KeyWellMap, IReadOnlyDictionary<string, double>?>(nameof(Heat));

    private const double KeyGap = 0.08;
    private const double CornerRadius = 0.12;

    private static readonly Typeface LegendFace = new(FontFamily.Default);
    private static readonly Typeface StrongLegendFace = new(FontFamily.Default, weight: FontWeight.SemiBold);

    static KeyWellMap()
    {
        AffectsRender<KeyWellMap>(LayoutProperty, LayerIdProperty, SelectedKeyIdProperty, HighlightKeyIdProperty, HeatProperty);
        AffectsMeasure<KeyWellMap>(LayoutProperty);
    }

    public KeyWellMap()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public EffectiveLayout? Layout
    {
        get => GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    public string LayerId
    {
        get => GetValue(LayerIdProperty);
        set => SetValue(LayerIdProperty, value);
    }

    public string? SelectedKeyId
    {
        get => GetValue(SelectedKeyIdProperty);
        set => SetValue(SelectedKeyIdProperty, value);
    }

    /// <summary>The key to press next, drawn filled with the accent colour.</summary>
    public string? HighlightKeyId
    {
        get => GetValue(HighlightKeyIdProperty);
        set => SetValue(HighlightKeyIdProperty, value);
    }

    /// <summary>A value from 0 to 1 per key id, drawn as a warm tint, such as each key's share of errors.</summary>
    public IReadOnlyDictionary<string, double>? Heat
    {
        get => GetValue(HeatProperty);
        set => SetValue(HeatProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Layout is null)
            return default;

        // Keep the keyboard's own proportions, filling whichever dimension is constrained.
        var extent = Extent(Layout.Pack);
        var width = double.IsInfinity(availableSize.Width) ? extent.Width * 48 : availableSize.Width;
        var height = width * extent.Height / extent.Width;
        if (!double.IsInfinity(availableSize.Height) && height > availableSize.Height)
        {
            height = availableSize.Height;
            width = height * extent.Width / extent.Height;
        }

        return new Size(width, height);
    }

    public override void Render(DrawingContext context)
    {
        if (Layout is not { } layout)
            return;

        // A map laid out with no room, such as one inside a hidden panel, has nothing to draw.
        var geometry = Fit(layout.Pack);
        if (geometry.Scale <= 0)
            return;

        var palette = Palette.For(ActualThemeVariant, this);
        foreach (var key in layout.Pack.Keys)
        {
            using (context.PushTransform(geometry.Rotation(key)))
                DrawKey(context, layout, key, geometry, palette);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Layout is not { } layout)
            return;

        var geometry = Fit(layout.Pack);
        var point = e.GetPosition(this);
        var hit = layout.Pack.Keys.LastOrDefault(key =>
            geometry.Rotation(key).Invert().Transform(point) is var local && geometry.Bounds(key).Contains(local));

        if (hit is { Remappable: true })
            SelectedKeyId = hit.Id;
        e.Handled = true;
    }

    private void DrawKey(DrawingContext context, EffectiveLayout layout, PhysicalKey key, MapGeometry geometry, Palette palette)
    {
        var bounds = geometry.Bounds(key).Deflate(KeyGap * geometry.Scale / 2);
        var effective = layout.Find(LayerId, key.Id);
        var changed = effective is { Change: not KeyChange.None };
        var selected = key.Id == SelectedKeyId;

        var highlighted = key.Id == HighlightKeyId;
        var heat = Heat?.GetValueOrDefault(key.Id) ?? 0;

        var fill = highlighted ? palette.Accent
            : heat > 0 ? new SolidColorBrush(palette.HeatColor, 0.15 + 0.7 * Math.Clamp(heat, 0, 1))
            : !key.Remappable ? palette.FixedKey
            : changed ? palette.ChangedKey
            : key.Cluster == Cluster.Function ? palette.FunctionKey
            : palette.Key;
        var border = selected ? new Pen(palette.Accent, 2.5) : new Pen(palette.Border, 1);
        context.DrawRectangle(fill, border, new RoundedRect(bounds, CornerRadius * geometry.Scale));

        var legend = effective is null ? layout.Pack.LegendFor(key.Id) : layout.Pack.LegendFor(effective.Action);
        var legendBrush = highlighted ? palette.OnAccent
            : !key.Remappable ? palette.FixedLegend
            : changed && heat <= 0 ? palette.Accent
            : palette.Legend;
        var size = geometry.Scale * (key.Cluster == Cluster.Function ? 0.22 : 0.28);

        if (effective?.TapHold is { } tapHold)
        {
            DrawCentred(context, legend, bounds.Center.WithY(bounds.Center.Y - size * 0.45), bounds.Width, size, legendBrush, strong: true);
            DrawCentred(context, $"hold {layout.Pack.LegendFor(tapHold.Hold.Name)}", bounds.Center.WithY(bounds.Center.Y + size * 0.75),
                bounds.Width, size * 0.7, palette.Muted, strong: false);
        }
        else
        {
            DrawCentred(context, legend, bounds.Center, bounds.Width, size, legendBrush, strong: changed);
        }

        // The original legend stays visible in the corner so a remapped key can still be found by its keycap.
        if (changed && effective is not null && key.Cluster != Cluster.Function)
        {
            var original = Text(layout.Pack.LegendFor(effective.DefaultAction), size * 0.55, palette.Muted, strong: false);
            context.DrawText(original, bounds.TopLeft + new Vector(size * 0.3, size * 0.15));
        }

        if (effective is { Macros.Count: > 0 })
        {
            var radius = geometry.Scale * 0.09;
            context.DrawEllipse(palette.Accent, null, bounds.TopRight + new Vector(-radius * 1.8, radius * 1.8), radius, radius);
        }
    }

    private static void DrawCentred(DrawingContext context, string text, Point centre, double maxWidth, double size, IBrush brush, bool strong)
    {
        var formatted = Text(text, size, brush, strong);

        // Long legends shrink to fit the keycap rather than spilling over its neighbours.
        var available = maxWidth * 0.86;
        if (formatted.Width > available)
            formatted = Text(text, size * available / formatted.Width, brush, strong);

        context.DrawText(formatted, centre - new Vector(formatted.Width / 2, formatted.Height / 2));
    }

    private static FormattedText Text(string text, double size, IBrush brush, bool strong) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, strong ? StrongLegendFace : LegendFace, size, brush);

    private MapGeometry Fit(KeyboardPack pack)
    {
        var extent = Extent(pack);
        var scale = Math.Min(Bounds.Width / extent.Width, Bounds.Height / extent.Height);
        var offset = new Vector(
            (Bounds.Width - extent.Width * scale) / 2 - extent.X * scale,
            (Bounds.Height - extent.Height * scale) / 2 - extent.Y * scale);
        return new MapGeometry(scale, offset);
    }

    // The smallest rectangle, in key units, that holds every key once rotated.
    private static Rect Extent(KeyboardPack pack)
    {
        var unit = new MapGeometry(1, default);
        var corners = pack.Keys.SelectMany(key =>
        {
            var bounds = unit.Bounds(key);
            var rotation = unit.Rotation(key);
            return new[] { bounds.TopLeft, bounds.TopRight, bounds.BottomLeft, bounds.BottomRight }.Select(corner => rotation.Transform(corner));
        }).ToList();

        var left = corners.Min(corner => corner.X);
        var top = corners.Min(corner => corner.Y);
        return new Rect(left, top, corners.Max(corner => corner.X) - left, corners.Max(corner => corner.Y) - top);
    }

    private readonly record struct MapGeometry(double Scale, Vector Offset)
    {
        public Rect Bounds(PhysicalKey key) =>
            new(Offset.X + key.X * Scale, Offset.Y + key.Y * Scale, key.Width * Scale, key.Height * Scale);

        public Matrix Rotation(PhysicalKey key)
        {
            if (key.Rotation == 0)
                return Matrix.Identity;

            var origin = new Vector(Offset.X + key.RotationOriginX * Scale, Offset.Y + key.RotationOriginY * Scale);
            return Matrix.CreateTranslation(-origin)
                * Matrix.CreateRotation(key.Rotation * Math.PI / 180)
                * Matrix.CreateTranslation(origin);
        }
    }

    private sealed record Palette(
        IBrush Key,
        IBrush FunctionKey,
        IBrush FixedKey,
        IBrush ChangedKey,
        IBrush Border,
        IBrush Legend,
        IBrush FixedLegend,
        IBrush Muted,
        IBrush Accent)
    {
        public Color HeatColor { get; } = Color.Parse("#E5484D");

        public IBrush OnAccent { get; } = Brushes.White;

        private static readonly Color FallbackAccent = Color.Parse("#2F6FEB");

        public static Palette For(ThemeVariant theme, StyledElement element)
        {
            var accent = element.TryFindResource("SystemAccentColor", theme, out var found) && found is Color color ? color : FallbackAccent;
            var accentTint = new SolidColorBrush(accent, theme == ThemeVariant.Dark ? 0.28 : 0.14);

            return theme == ThemeVariant.Dark
                ? new Palette(
                    Brush("#2B2D31"), Brush("#24262A"), Brush("#111214"), accentTint,
                    Brush("#44474E"), Brush("#E6E7EA"), Brush("#9A9DA3"), Brush("#8B8F96"), new SolidColorBrush(accent))
                : new Palette(
                    Brush("#FFFFFF"), Brush("#F3F4F6"), Brush("#2A2C30"), accentTint,
                    Brush("#C9CDD3"), Brush("#1F2328"), Brush("#E6E7EA"), Brush("#6B7079"), new SolidColorBrush(accent));
        }

        private static SolidColorBrush Brush(string hex) => new(Color.Parse(hex));
    }
}
