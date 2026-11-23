using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using DbClient.App.Themes;
using DbClient.Core;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace DbClient.App.Views;

internal sealed class SemanticColorizer : DocumentColorizingTransformer
{
    private readonly TextEditor _box;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<SqlSpan>>> _classify;
    private readonly TextSegmentCollection<Segment> _segments;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private CancellationTokenSource? _cts;
    private Dictionary<SqlRole, Brush> _brushes = [];

    public SemanticColorizer(TextEditor box, Func<string, CancellationToken, Task<IReadOnlyList<SqlSpan>>> classify)
    {
        _box = box;
        _classify = classify;
        _segments = new TextSegmentCollection<Segment>(box.Document);
        box.TextArea.TextView.LineTransformers.Add(this);
        box.TextChanged += (_, _) => Restart();
        _timer.Tick += (_, _) => _ = RunAsync();
    }

    public void Restart()
    {
        _timer.Stop();
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        _cts?.Cancel();
    }

    public void Reset()
    {
        _segments.Clear();
        Restart();
    }

    public void SetColors(SqlColors colors)
    {
        _brushes = new()
        {
            [SqlRole.Table] = Frozen(colors.Table), [SqlRole.Alias] = Frozen(colors.Text),
            [SqlRole.Column] = Frozen(colors.Column), [SqlRole.Function] = Frozen(colors.Function),
        };
        _box.TextArea.TextView.Redraw();
    }

    private async Task RunAsync()
    {
        Stop();
        var cts = _cts = new CancellationTokenSource();
        var text = _box.Text;
        IReadOnlyList<SqlSpan> spans;
        try
        {
            spans = text.Length <= 200_000 ? await _classify(text, cts.Token) : [];
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (cts.IsCancellationRequested || _box.Text != text) return;
        _segments.Clear();
        foreach (var span in spans) _segments.Add(new Segment { StartOffset = span.Start, Length = span.Length, Role = span.Role });
        _box.TextArea.TextView.Redraw();
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        foreach (var s in _segments.FindOverlappingSegments(line.Offset, line.Length))
        {
            if (!_brushes.TryGetValue(s.Role, out var brush)) continue;
            var (start, end) = (Math.Max(s.StartOffset, line.Offset), Math.Min(s.EndOffset, line.EndOffset));
            if (end <= start) continue;
            var bold = s.Role == SqlRole.Function;
            ChangeLinePart(start, end, e =>
            {
                e.TextRunProperties.SetForegroundBrush(brush);
                if (!bold) return;
                var t = e.TextRunProperties.Typeface;
                e.TextRunProperties.SetTypeface(new Typeface(t.FontFamily, t.Style, FontWeights.Bold, t.Stretch));
            });
        }
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private sealed class Segment : TextSegment
    {
        public SqlRole Role { get; init; }
    }
}
