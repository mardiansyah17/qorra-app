namespace DbClient.Core.Services;

public static class AppPaths
{
    public static string DataDirectory { get; } = OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Qorra")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DbClient");

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
