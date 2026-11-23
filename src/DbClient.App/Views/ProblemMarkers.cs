using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace DbClient.App.Views;

internal sealed class Problem : TextSegment
{
    public required string Message { get; init; }
    public bool IsWarning { get; init; }
    public bool FromRun { get; init; }
    public bool FromServer { get; init; }
}

internal sealed class ProblemRenderer(TextSegmentCollection<Problem> problems) : IBackgroundRenderer
{
    public Pen? Error { get; set; }
    public Pen? Warning { get; set; }
    public KnownLayer Layer => KnownLayer.Selection;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!textView.VisualLinesValid || textView.VisualLines.Count == 0 || problems.Count == 0) return;
        var start = textView.VisualLines[0].FirstDocumentLine.Offset;
        var end = textView.VisualLines[^1].LastDocumentLine.EndOffset;
        foreach (var problem in problems.FindOverlappingSegments(start, end - start).OrderBy(p => !p.IsWarning))
        {
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, problem))
            {
                var wave = new StreamGeometry();
                using (var g = wave.Open())
                {
                    var y = rect.Bottom - 1;
                    g.BeginFigure(new Point(rect.Left, y), false, false);
                    var up = true;
                    for (var x = rect.Left + 2; x <= rect.Right + 2; x += 2, up = !up) g.LineTo(new Point(x, up ? y - 2 : y), true, false);
                }
                wave.Freeze();
                drawingContext.DrawGeometry(null, problem.IsWarning ? Warning : Error, wave);
            }
        }
    }
}
