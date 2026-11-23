using System.Data.Common;
using DbClient.Core.Services;

namespace DbClient.Tests.Core;

public class ErrorLogTests
{
    private sealed class FakeDbException(string message) : DbException(message)
    {
        public override string? SqlState => "42P01";
    }

    private static Exception Thrown(Exception e)
    {
        try { throw e; }
        catch (Exception caught) { return caught; }
    }

    [Fact]
    public void Format_drops_messages_that_may_carry_data_but_keeps_type_state_and_stack()
    {
        var ex = Thrown(new InvalidOperationException("wrap: secret_table", new FakeDbException("relation \"customers\" password=hunter2")));

        var text = ErrorLog.Format(ex);

        Assert.Contains("System.InvalidOperationException", text);
        Assert.Contains("FakeDbException (SQLSTATE 42P01)", text);
        Assert.Contains(nameof(Thrown), text);
        Assert.DoesNotContain("customers", text);
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("secret_table", text);
    }

    [Fact]
    public void Write_collapses_repeats_and_never_throws()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dbclient-errorlog-" + Guid.NewGuid().ToString("N"));
        var old = ErrorLog.FilePath;
        ErrorLog.FilePath = Path.Combine(dir, "error.log");
        try
        {
            var ex = Thrown(new NullReferenceException());
            for (var i = 0; i < 5; i++) ErrorLog.Write("test", ex);
            ErrorLog.Flush();

            var text = File.ReadAllText(ErrorLog.FilePath);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "NullReferenceException"));
            Assert.Contains("repeated 4 more times", text);
        }
        finally
        {
            ErrorLog.FilePath = old;
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Operational_errors_are_not_unexpected()
    {
        Assert.True(ErrorLog.IsOperational(new FakeDbException("x")));
        Assert.True(ErrorLog.IsOperational(new IOException()));
        Assert.False(ErrorLog.IsOperational(new NullReferenceException()));
    }
}
