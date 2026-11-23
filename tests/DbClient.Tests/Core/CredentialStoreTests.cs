using DbClient.Core.Services;

namespace DbClient.Tests.Core;

public sealed class CredentialStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dbclient-test-").FullName;
    private string FilePath => Path.Combine(_dir, "credentials.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly byte[] TestKey = [.. Enumerable.Range(1, 32).Select(i => (byte)i)];

    private CredentialStore New() => OperatingSystem.IsWindows() ? new(FilePath) : new(FilePath, () => TestKey);

    [Fact]
    public void MasterKey_RoundTrips_AndAnotherKeyCannotRead()
    {
        var id = Guid.NewGuid();
        new CredentialStore(FilePath, () => TestKey).Set(id, CredentialStore.Password, "s3cr3t-päss");

        Assert.DoesNotContain("s3cr3t", File.ReadAllText(FilePath));
        Assert.Equal("s3cr3t-päss", new CredentialStore(FilePath, () => TestKey).Get(id, CredentialStore.Password));
        Assert.Null(new CredentialStore(FilePath, () => new byte[32]).Get(id, CredentialStore.Password));
    }

    [Theory]
    [InlineData("AAECAwQFBgcICQoLDA0ODw==")]
    [InlineData("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8gISIj")]
    public void MasterKey_GarbageEntry_GetReturnsNull(string value)
    {
        var id = Guid.NewGuid();
        File.WriteAllText(FilePath, $$"""{ "{{id}}/password": "{{value}}" }""");

        Assert.Null(new CredentialStore(FilePath, () => TestKey).Get(id, CredentialStore.Password));
    }

    [Fact]
    public void Set_PersistsEncrypted_AndNewInstanceCanRead()
    {
        var id = Guid.NewGuid();
        const string secret = "s3cr3t-päss;word";

        New().Set(id, CredentialStore.Password, secret);

        Assert.DoesNotContain("s3cr3t", File.ReadAllText(FilePath));
        Assert.Equal(secret, New().Get(id, CredentialStore.Password));
    }

    [Fact]
    public void SetNullOrEmpty_RemovesSecret()
    {
        var store = New();
        var id = Guid.NewGuid();
        store.Set(id, CredentialStore.Password, "x");

        store.Set(id, CredentialStore.Password, "");

        Assert.Null(New().Get(id, CredentialStore.Password));
    }

    [Fact]
    public void RemoveAll_OnlyRemovesThatProfile()
    {
        var store = New();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        store.Set(a, CredentialStore.Password, "a-pw");
        store.Set(a, "ssh-passphrase", "a-ssh");
        store.Set(b, CredentialStore.Password, "b-pw");

        store.RemoveAll(a);

        var reloaded = New();
        Assert.Null(reloaded.Get(a, CredentialStore.Password));
        Assert.Null(reloaded.Get(a, "ssh-passphrase"));
        Assert.Equal("b-pw", reloaded.Get(b, CredentialStore.Password));
    }

    [Fact]
    public void TwoInstances_SetDifferentProfiles_NeitherIsLost()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        var first = New();
        var second = New();
        Assert.Null(first.Get(a, CredentialStore.Password));
        Assert.Null(second.Get(b, CredentialStore.Password));

        first.Set(a, CredentialStore.Password, "a-pw");
        second.Set(b, CredentialStore.Password, "b-pw");

        var third = New();
        Assert.Equal("a-pw", third.Get(a, CredentialStore.Password));
        Assert.Equal("b-pw", third.Get(b, CredentialStore.Password));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void CorruptFile_IsBackedUp_AndStoreStartsEmpty()
    {
        File.WriteAllText(FilePath, "{ not json");
        var store = New();
        var id = Guid.NewGuid();

        Assert.Null(store.Get(id, CredentialStore.Password));
        store.Set(id, CredentialStore.Password, "pw");

        Assert.Equal("pw", New().Get(id, CredentialStore.Password));
        var backup = Assert.Single(Directory.GetFiles(_dir, "credentials.json.corrupt-*"));
        Assert.Equal("{ not json", File.ReadAllText(backup));
    }

    [Theory]
    [InlineData("!!! not base64 !!!")]
    [InlineData("AAECAwQFBgcICQoLDA0ODw==")]
    public void UndecryptableEntry_GetReturnsNull(string value)
    {
        var id = Guid.NewGuid();
        File.WriteAllText(FilePath, $$"""{ "{{id}}/password": "{{value}}" }""");

        Assert.Null(New().Get(id, CredentialStore.Password));
    }
}
