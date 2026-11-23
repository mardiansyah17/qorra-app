namespace DbClient.Core;

public static class SessionOptions
{
    public static int ConnectTimeout { get; set; } = 10;

    public static int KeepAlive { get; set; } = 30;

    public static int CommandTimeout { get; set; } = 30;

    public static bool Reconnect { get; set; } = true;

    public static bool UseSshAgent { get; set; }

    public static bool ShareTunnels { get; set; }
}
