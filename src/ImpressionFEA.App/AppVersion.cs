using System.Reflection;

namespace ImpressionFEA.App;

/// <summary>Release number (Directory.Build.props &lt;Version&gt;) and build stamp of the running app.</summary>
public static class AppVersion
{
    /// <summary>e.g. "v0.4.0 · build 20261011.0215".</summary>
    public static string Display { get; } = Compute();

    private static string Compute()
    {
        var asm = typeof(AppVersion).Assembly;
        string info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? asm.GetName().Version?.ToString(3) ?? "0.0.0";
        int plus = info.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? $"v{info}" : $"v{info[..plus]} · build {info[(plus + 1)..]}";
    }
}
