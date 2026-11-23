using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DbClient.App.Controls;

public sealed class Spinner : FrameworkElement
{
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Spinner),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly RotateTransform _rotate = new();

    public Spinner()
    {
        Width = Height = 14;
        RenderTransform = _rotate;
        RenderTransformOrigin = new Point(0.5, 0.5);
        SetResourceReference(StrokeProperty, "Brush.Accent");
        IsVisibleChanged += (_, _) =>
        {
            var spin = IsVisible
                ? new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(800)) { RepeatBehavior = RepeatBehavior.Forever }
                : null;
            _rotate.BeginAnimation(RotateTransform.AngleProperty, spin);
        };
    }

    public Brush? Stroke
    {
        get => (Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var thickness = Math.Max(1.5, ActualWidth / 8);
        var r = (Math.Min(ActualWidth, ActualHeight) - thickness) / 2;
        if (r <= 0) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);

        var track = new Pen(Stroke, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.PushOpacity(0.2);
        dc.DrawEllipse(null, track, c, r, r);
        dc.Pop();

        var start = new Point(c.X, c.Y - r);
        var end = new Point(c.X + r, c.Y);
        var arc = new StreamGeometry();
        using (var g = arc.Open())
        {
            g.BeginFigure(start, false, false);
            g.ArcTo(end, new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
        }
        arc.Freeze();
        dc.DrawGeometry(null, track, arc);
    }
}
