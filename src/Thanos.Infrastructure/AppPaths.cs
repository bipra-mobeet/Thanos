namespace Thanos.Infrastructure;

public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Thanos");
    public static string Database => Path.Combine(Root, "thanos.db");
    public static string Logs => Path.Combine(Root, "Logs");
    public static string Thumbnails => Path.Combine(Root, "Thumbnails");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Thumbnails);
    }
}
