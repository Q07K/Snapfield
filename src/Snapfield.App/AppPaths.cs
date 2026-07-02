using System.IO;

namespace Snapfield.App;

/// <summary>Resolves where Snapfield keeps its per-user data.</summary>
public static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Snapfield");

    public static string LayoutFile => Path.Combine(DataDir, "layout.json");
}
