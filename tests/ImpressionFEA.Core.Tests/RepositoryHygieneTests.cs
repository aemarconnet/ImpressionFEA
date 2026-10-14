using System.Text.RegularExpressions;

namespace ImpressionFEA.Core.Tests;

/// <summary>What the public repository must (not) contain.</summary>
public class RepositoryHygieneTests
{
    private static string? Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ImpressionFEA.slnx"))) dir = dir.Parent;
        return dir?.FullName;
    }

    /// <summary>Commercial forging-simulation products are not named in the samples or the code (case-sensitive: "deform" is a verb here).</summary>
    [Fact]
    public void SamplesAndSourcesNameNoCommercialSimulationSoftware()
    {
        if (Root() is not { } root) return;
        var names = new Regex(@"\bDEFORM\b|DEFORM-[23]D|\bQForm\b|\bSimufact\b|FORGE\s*NxT|\bFORGE®|\bTransvalor\b|\bAbaqus\b|\bANSYS\b|\bLS-DYNA\b|\bMSC\.?\s?Marc\b", RegexOptions.CultureInvariant);
        var hits = new List<string>();
        foreach (var folder in new[] { "samples", "src", "tests", "native/cad", "tools" })
        {
            string dir = Path.Combine(root, folder);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.EndsWith(nameof(RepositoryHygieneTests) + ".cs", StringComparison.Ordinal))
                    continue;
                if (Path.GetExtension(file).ToLowerInvariant() is not (".cs" or ".xaml" or ".json" or ".md" or ".txt" or ".cpp" or ".ps1" or ".iss")) continue;
                foreach (Match m in names.Matches(File.ReadAllText(file))) hits.Add($"{Path.GetRelativePath(root, file)}: {m.Value}");
            }
        }

        Assert.Empty(hits);
    }

    /// <summary>AI assistant files, chat history, the private roadmap and licensed reference material never reach GitHub.</summary>
    [Fact]
    public void PrivateFilesAreIgnored()
    {
        if (Root() is not { } root) return;
        var lines = File.ReadAllLines(Path.Combine(root, ".gitignore")).Select(l => l.Trim()).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in new[] { ".claude/", "CLAUDE.md", "AGENTS.md", "FUTURE_WORK.md", "reference/" })
            Assert.Contains(entry, lines);
    }
}
