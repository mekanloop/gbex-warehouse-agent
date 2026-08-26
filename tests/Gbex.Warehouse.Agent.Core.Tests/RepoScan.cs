namespace Gbex.Warehouse.Agent.Core.Tests;

/// <summary>Locates the repo root at test time (walks up from the test binary's directory looking for the .sln file) so structural/source-scan tests can read source files without a hardcoded absolute path.</summary>
public static class RepoScan
{
    public static string RepoRoot { get; } = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && dir is not null; i++)
        {
            if (Directory.GetFiles(dir, "*.sln").Length > 0)
            {
                return dir;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException("Could not locate repo root (.sln file) walking up from " + AppContext.BaseDirectory);
    }

    public static IEnumerable<string> AllSourceFiles(params string[] subdirectories)
    {
        foreach (var subdir in subdirectories)
        {
            var full = Path.Combine(RepoRoot, subdir);
            if (!Directory.Exists(full)) continue;
            foreach (var file in Directory.EnumerateFiles(full, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || Path.GetFileName(file).StartsWith("._"))
                {
                    continue;
                }
                yield return file;
            }
        }
    }
}
