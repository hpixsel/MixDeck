namespace MixDeck;

public static class Log
{
    static readonly object Gate = new();
    public static string FilePath => Path.Combine(Model.ConfigStore.Directory, "log.txt");

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 1_000_000) fi.Delete();
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
