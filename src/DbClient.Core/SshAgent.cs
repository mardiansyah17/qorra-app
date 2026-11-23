using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Security;

namespace DbClient.Core;

internal static class SshAgent
{
    private const string PipeName = "openssh-ssh-agent";
    private const byte RequestIdentities = 11, IdentitiesAnswer = 12, SignRequest = 13, SignResponse = 14;
    private const uint RsaSha256 = 2, RsaSha512 = 4;

    public static IReadOnlyList<HostAlgorithm> Keys()
    {
        byte[] reply;
        try { reply = Call([RequestIdentities]); }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException) { return []; }
        if (reply.Length < 5 || reply[0] != IdentitiesAnswer) return [];
        var keys = new List<HostAlgorithm>();
        var pos = 1;
        var count = ReadUInt(reply, ref pos);
        for (var i = 0; i < count; i++)
        {
            var blob = ReadString(reply, ref pos);
            ReadString(reply, ref pos);
            var p = 0;
            var type = Encoding.ASCII.GetString(ReadString(blob, ref p));
            if (type == "ssh-rsa")
            {
                keys.Add(new AgentKey("rsa-sha2-512", blob, RsaSha512));
                keys.Add(new AgentKey("rsa-sha2-256", blob, RsaSha256));
            }
            keys.Add(new AgentKey(type, blob, 0));
        }
        return keys;
    }

    private static byte[] Sign(byte[] blob, byte[] data, uint flags)
    {
        var body = new MemoryStream();
        body.WriteByte(SignRequest);
        WriteString(body, blob);
        WriteString(body, data);
        Span<byte> f = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(f, flags);
        body.Write(f);
        var reply = Call(body.ToArray());
        if (reply.Length < 5 || reply[0] != SignResponse) throw new InvalidOperationException("The SSH agent refused to sign.");
        var pos = 1;
        return ReadString(reply, ref pos);
    }

    private static byte[] Call(byte[] message)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
        pipe.Connect(2000);
        var frame = new byte[4 + message.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)message.Length);
        message.CopyTo(frame, 4);
        pipe.Write(frame);
        var header = new byte[4];
        pipe.ReadExactly(header);
        var reply = new byte[BinaryPrimitives.ReadUInt32BigEndian(header)];
        pipe.ReadExactly(reply);
        return reply;
    }

    private static uint ReadUInt(byte[] b, ref int pos)
    {
        var v = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(pos));
        pos += 4;
        return v;
    }

    private static byte[] ReadString(byte[] b, ref int pos)
    {
        var len = (int)ReadUInt(b, ref pos);
        var s = b.AsSpan(pos, len).ToArray();
        pos += len;
        return s;
    }

    private static void WriteString(Stream s, byte[] value)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)value.Length);
        s.Write(len);
        s.Write(value);
    }

    private sealed class AgentKey(string name, byte[] blob, uint flags) : HostAlgorithm(name)
    {
        public override byte[] Data => blob;
        public override byte[] Sign(byte[] data) => SshAgent.Sign(blob, data, flags);
        public override bool VerifySignature(byte[] data, byte[] signature) => throw new NotSupportedException();
    }

    public sealed class KeySource(IReadOnlyList<HostAlgorithm> keys) : IPrivateKeySource
    {
        public IReadOnlyCollection<HostAlgorithm> HostKeyAlgorithms => keys;
    }
}
