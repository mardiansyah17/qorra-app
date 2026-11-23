using DbClient.ViewModels;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using DbClient.Core.Models;

namespace DbClient.App.Controls;

public sealed class EngineBadge : Border
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(DatabaseKind), typeof(EngineBadge),
        new PropertyMetadata(DatabaseKind.PostgreSql, (d, _) => ((EngineBadge)d).Update()));

    private static readonly Dictionary<DatabaseKind, Geometry> Logos = [];

    private readonly Path _logo = new() { Stretch = Stretch.Uniform, SnapsToDevicePixels = false };

    public EngineBadge()
    {
        Width = Height = 28;
        Child = _logo;
        Update();
    }

    public DatabaseKind Kind
    {
        get => (DatabaseKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        _logo.Margin = new Thickness(Math.Round(size * 0.12));
        CornerRadius = new CornerRadius(Math.Round(size * 0.24));
    }

    private void Update()
    {
        var key = "Brush.Engine." + (Kind is DatabaseKind.PostgreSql or DatabaseKind.MySql or DatabaseKind.SqlServer ? Kind.ToString() : "Sqlite");
        _logo.Data = Logo(Kind);
        _logo.SetResourceReference(Shape.FillProperty, key);
        SetResourceReference(BackgroundProperty, key + ".Subtle");
        AutomationProperties.SetName(this, ProfileText.KindName(Kind));
    }

    private static Geometry Logo(DatabaseKind kind)
    {
        if (Logos.TryGetValue(kind, out var cached)) return cached;
        var geometry = Geometry.Parse(kind switch
        {
            DatabaseKind.PostgreSql => EngineLogos.PostgreSql,
            DatabaseKind.MySql => EngineLogos.MySql,
            DatabaseKind.SqlServer => EngineLogos.SqlServer,
            _ => EngineLogos.Sqlite,
        });
        if (kind == DatabaseKind.MySql)
            geometry = Geometry.Combine(geometry, new RectangleGeometry(new Rect(0, 0, 24, 13.1)), GeometryCombineMode.Intersect, null);
        geometry.Freeze();
        return Logos[kind] = geometry;
    }
}
