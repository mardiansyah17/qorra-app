using System.Data.Common;
using System.Runtime.InteropServices;
using System.Text;

namespace DbClient.Core.Services;

public static class ErrorLog
{
    private const long MaxBytes = 1_000_000;
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(10);
    private static readonly Lock Gate = new();
    private static string? _last;
    private static DateTime _lastAt;
    private static int _repeats;
    private static bool _sessionStarted;

    public static string FilePath { get; set; } = Path.Combine(AppPaths.DataDirectory, "logs", "error.log");

    public static void Write(string source, Exception exception)
    {
        try
        {
            var body = Format(exception);
            lock (Gate)
            {
                var now = DateTime.Now;
                if (body == _last && now - _lastAt < RepeatWindow)
                {
                    _repeats++;
                    _lastAt = now;
                    return;
                }
                var text = new StringBuilder(TakeRepeats());
                if (!_sessionStarted)
                {
                    _sessionStarted = true;
                    text.Append($"===== Session {now:yyyy-MM-dd HH:mm:ss} · Qorra {System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version} · " +
                                $"{RuntimeInformation.FrameworkDescription} · {RuntimeInformation.OSDescription} =====\n\n");
                }
                text.Append($"[{now:yyyy-MM-dd HH:mm:ss.fff}] {source}\n{body}\n");
                _last = body;
                _lastAt = now;
                Append(text.ToString());
            }
        }
        catch (Exception)
        {
        }
    }

    public static void Unexpected(string source, Exception exception)
    {
        if (!IsOperational(exception)) Write(source + " (caught)", exception);
    }

    public static bool IsOperational(Exception e) =>
        e is DbException or IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException
            or System.Net.Sockets.SocketException or System.Security.Authentication.AuthenticationException
            or System.Security.Cryptography.CryptographicException
        || e.GetType().Namespace?.StartsWith("Renci.SshNet", StringComparison.Ordinal) == true
        || e is AggregateException { InnerExceptions: var inner } && inner.All(IsOperational);

    public static void Flush()
    {
        try
        {
            lock (Gate)
                if (TakeRepeats() is { Length: > 0 } line) Append(line);
        }
        catch (Exception)
        {
        }
    }

    private static string TakeRepeats()
    {
        var line = _repeats > 0 ? $"  (previous entry repeated {_repeats} more times)\n\n" : "";
        _repeats = 0;
        return line;
    }

    private static void Append(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
            File.Move(FilePath, Path.ChangeExtension(FilePath, ".1.log"), overwrite: true);
        File.AppendAllText(FilePath, text);
    }

    public static string Format(Exception exception)
    {
        var text = new StringBuilder();
        Add(exception, 0);
        return text.ToString();

        void Add(Exception e, int depth)
        {
            var indent = new string(' ', depth * 2);
            text.Append(indent).Append(depth == 0 ? "" : "inner: ").Append(e.GetType().FullName);
            if (e is DbException { SqlState: { Length: > 0 } state }) text.Append(" (SQLSTATE ").Append(state).Append(')');
            if (IsSafeMessage(e)) text.Append(": ").Append(e.Message);
            text.Append('\n');
            if (e.StackTrace is { } trace)
                foreach (var line in trace.Split('\n')) text.Append(indent).Append(line.TrimEnd()).Append('\n');
            IEnumerable<Exception> inner = e is AggregateException aggregate ? aggregate.InnerExceptions : e.InnerException is { } one ? [one] : [];
            foreach (var child in inner) Add(child, depth + 1);
        }
    }

    private static bool IsSafeMessage(Exception e) =>
        e is not DbException && e.TargetSite?.DeclaringType?.Namespace is { } ns &&
        (ns.StartsWith("System.Windows", StringComparison.Ordinal) || ns.StartsWith("MS.Internal", StringComparison.Ordinal));
}
