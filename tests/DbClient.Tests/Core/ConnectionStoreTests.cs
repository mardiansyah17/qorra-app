using DbClient.Core.Models;
using DbClient.Core.Services;

namespace DbClient.Tests.Core;

public sealed class ConnectionStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dbclient-test-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        var store = new ConnectionStore(Path.Combine(_dir, "nope.json"));
        Assert.Empty(store.Load());
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAllFields()
    {
        var path = Path.Combine(_dir, "sub", "connections.json");
        var original = new ConnectionProfile
        {
            Name = "prod", Kind = DatabaseKind.MySql, Host = "db.example.com", Port = 3307,
            User = "app", Database = "shop", SslMode = SslMode.VerifyFull, Color = "#E5484D",
        };

        new ConnectionStore(path).Save([original]);
        var loaded = Assert.Single(new ConnectionStore(path).Load());

        Assert.Equivalent(original, loaded, strict: true);
        Assert.Contains("\"VerifyFull\"", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public void Save_WhenReplaceFails_Throws_AndKeepsPreviousFile()
    {
        var path = Path.Combine(_dir, "connections.json");
        var store = new ConnectionStore(path);
        store.Save([new ConnectionProfile { Name = "old" }]);
        if (OperatingSystem.IsWindows()) File.SetAttributes(path, FileAttributes.ReadOnly);
        else File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var ex = Record.Exception(() => store.Save([new ConnectionProfile { Name = "new" }]));
        if (OperatingSystem.IsWindows()) File.SetAttributes(path, FileAttributes.Normal);
        else File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Assert.True(ex is IOException or UnauthorizedAccessException, $"unexpected: {ex}");
        Assert.Equal("old", Assert.Single(store.Load()).Name);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Load_CorruptFile_ThrowsWithPath_AndLeavesFileAlone()
    {
        var path = Path.Combine(_dir, "connections.json");
        File.WriteAllText(path, "[{ broken");

        var ex = Assert.Throws<InvalidDataException>(() => new ConnectionStore(path).Load());

        Assert.Contains(path, ex.Message);
        Assert.Equal("[{ broken", File.ReadAllText(path));
    }

    [Fact]
    public void Save_OverwritesExistingFile()
    {
        var store = new ConnectionStore(Path.Combine(_dir, "connections.json"));
        store.Save([new ConnectionProfile { Name = "a" }, new ConnectionProfile { Name = "b" }]);

        store.Save([new ConnectionProfile { Name = "c" }]);

        Assert.Equal("c", Assert.Single(store.Load()).Name);
    }
}
