using Snapfield.Core.Persistence;

namespace Snapfield.App;

/// <summary>Resolves where Snapfield keeps its per-user data.</summary>
public static class AppPaths
{
    // Single source of truth lives in Core so Platform (network sessions) and the
    // App agree on the same file.
    public static string LayoutFile => LayoutStore.DefaultPath;
}
