using DbClient.Core.Models;
using Renci.SshNet;

namespace DbClient.Core;

internal sealed class SshTunnel : IDisposable
{
    private readonly SshClient _client;
    private readonly ForwardedPortLocal _forward;

    private SshTunnel(SshClient client, ForwardedPortLocal forward)
    {
        _client = client;
        _forward = forward;
    }

    public int LocalPort => (int)_forward.BoundPort;

    private bool IsAlive => _client.IsConnected && _forward.IsStarted;

    private sealed class Shared(Task<SshTunnel> opening)
    {
        public Task<SshTunnel> Opening { get; } = opening;
        public int Users;
    }

    private static readonly Dictionary<string, Shared> Pool = [];

    public static async Task<Lease> AcquireAsync(ConnectionProfile profile, string? secret, int remotePort, CancellationToken ct)
    {
        if (!SessionOptions.ShareTunnels) return new Lease(await OpenAsync(profile, secret, remotePort, ct), null);
        var key = string.Join('|', profile.Id, profile.SshHost.Trim().ToLowerInvariant(), profile.SshPort, profile.SshUser, profile.SshKeyFile,
            profile.Host.ToLowerInvariant(), remotePort);
        Shared shared;
        lock (Pool)
        {
            if (!Pool.TryGetValue(key, out shared!) || shared.Opening.IsFaulted || shared.Opening.IsCanceled
                || shared.Opening.IsCompletedSuccessfully && !shared.Opening.Result.IsAlive)
            {
                Pool[key] = shared = new Shared(OpenAsync(profile, secret, remotePort, CancellationToken.None));
            }
            shared.Users++;
        }
        try
        {
            return new Lease(await shared.Opening.WaitAsync(ct), () => Release(key, shared));
        }
        catch
        {
            Release(key, shared);
            throw;
        }
    }

    private static void Release(string key, Shared shared)
    {
        lock (Pool)
        {
            if (--shared.Users > 0) return;
            if (Pool.TryGetValue(key, out var current) && current == shared) Pool.Remove(key);
        }
        _ = shared.Opening.ContinueWith(t => t.Result.Dispose(), TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    public sealed class Lease(SshTunnel tunnel, Action? release) : IDisposable
    {
        private int _disposed;
        public int LocalPort => tunnel.LocalPort;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            if (release is null) tunnel.Dispose();
            else release();
        }
    }

    public static async Task<SshTunnel> OpenAsync(ConnectionProfile profile, string? secret, int remotePort, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(profile.SshHost)) throw new InvalidOperationException("Enter the SSH host.");
        var user = profile.SshUser;
        var auth = new List<AuthenticationMethod>();
        if (profile.SshKeyFile.Length > 0)
        {
            auth.Add(new PrivateKeyAuthenticationMethod(user, new PrivateKeyFile(profile.SshKeyFile, string.IsNullOrEmpty(secret) ? null : secret)));
        }
        if (SessionOptions.UseSshAgent && SshAgent.Keys() is { Count: > 0 } agentKeys)
            auth.Add(new PrivateKeyAuthenticationMethod(user, new SshAgent.KeySource(agentKeys)));
        if (profile.SshKeyFile.Length == 0)
        {
            var keyboard = new KeyboardInteractiveAuthenticationMethod(user);
            keyboard.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts) prompt.Response = secret ?? "";
            };
            auth.Add(new PasswordAuthenticationMethod(user, secret ?? ""));
            auth.Add(keyboard);
        }

        var info = new ConnectionInfo(profile.SshHost.Trim(), profile.SshPort > 0 ? profile.SshPort : 22, user, [.. auth])
        {
            Timeout = TimeSpan.FromSeconds(SessionOptions.ConnectTimeout),
        };
        var client = new SshClient(info)
        {
            KeepAliveInterval = SessionOptions.KeepAlive > 0 ? TimeSpan.FromSeconds(SessionOptions.KeepAlive) : Timeout.InfiniteTimeSpan,
        };
        string? changedKey = null;
        client.HostKeyReceived += (_, e) =>
        {
            var key = "SHA256:" + e.FingerPrintSHA256;
            if (profile.SshHostKey is null) profile.SshHostKey = key;
            else if (profile.SshHostKey != key)
            {
                changedKey = key;
                e.CanTrust = false;
            }
        };

        try
        {
            await client.ConnectAsync(ct);
            var forward = new ForwardedPortLocal("127.0.0.1", 0, profile.Host, (uint)remotePort);
            client.AddForwardedPort(forward);
            forward.Start();
            return new SshTunnel(client, forward);
        }
        catch (Exception) when (changedKey is not null)
        {
            client.Dispose();
            throw new InvalidOperationException(
                $"The SSH server's host key changed (now {changedKey}, saved {profile.SshHostKey}). This can mean someone is intercepting " +
                "the connection. If the server was reinstalled, choose Forget next to the host key in the connection's SSH settings.");
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _forward.Dispose();
        _client.Dispose();
    }
}
