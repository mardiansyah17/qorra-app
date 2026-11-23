namespace DbClient.Core.Services;

public static class AppPaths
{
    public static string DataDirectory { get; } = ResolveDataDirectory();

    private static string ResolveDataDirectory()
    {
        if (OperatingSystem.IsMacOS())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Qorra");
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "Qorra");
        var old = Path.Combine(appData, "DbClient");
        if (Directory.Exists(dir) || !Directory.Exists(old)) return dir;
        try
        {
            Directory.Move(old, dir);
            return dir;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return old;
        }
    }

    private static readonly Mutex StoreMutex = new(false, @"Local\DbClient.Store");

    internal static T Locked<T>(Func<T> action)
    {
        try { StoreMutex.WaitOne(); }
        catch (AbandonedMutexException) { }
        try { return action(); }
        finally { StoreMutex.ReleaseMutex(); }
    }

    internal static void Locked(Action action) => Locked(() => { action(); return 0; });

    internal static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(fs))
            {
                writer.Write(content);
                writer.Flush();
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}
