namespace ResearchScout.Cli;

/// <summary>
/// Loads KEY=VALUE lines from a .env file into environment variables, so API keys can live in a local file
/// (which .gitignore keeps out of git). Variables that are already set are not overwritten.
/// </summary>
internal static class DotEnv
{
    /// <summary>Looks for .env in the current folder and its parents. Returns the file used, or null.</summary>
    public static string? Load()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, ".env");
            if (File.Exists(path))
            {
                LoadFile(path);
                return path;
            }
        }

        return null;
    }

    private static void LoadFile(string path)
    {
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim().Trim('"');

            if (value.Length > 0 && Environment.GetEnvironmentVariable(key) is null)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }
}
