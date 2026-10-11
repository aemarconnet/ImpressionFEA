using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace ImpressionFEA.App.Export;

/// <summary>
/// Finds an FFmpeg executable (used only for WebM video): next to the app, in tools\ffmpeg, on the PATH, or installed
/// by winget. FFmpeg is optional; without it videos are written as PNG frame sequences.
/// </summary>
public static class FfmpegLocator
{
    private static string? _cached;
    private static bool _searched;

    public static string? Find()
    {
        if (_searched) return _cached;
        _searched = true;
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "bin", "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin", "ffmpeg.exe"),
        };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            candidates.Add(Path.Combine(dir.Trim('"'), "ffmpeg.exe"));
        }

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.Add(Path.Combine(local, "Microsoft", "WinGet", "Links", "ffmpeg.exe"));
        string pkgs = Path.Combine(local, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(pkgs))
        {
            foreach (var d in Directory.GetDirectories(pkgs, "*FFmpeg*"))
            {
                candidates.AddRange(Directory.EnumerateFiles(d, "ffmpeg.exe", SearchOption.AllDirectories));
            }
        }

        _cached = candidates.FirstOrDefault(File.Exists);
        return _cached;
    }
}

/// <summary>Streams raw BGRA frames into FFmpeg, which encodes VP9 into a WebM file.</summary>
public sealed class WebmWriter : IDisposable
{
    private readonly Process _process;
    private readonly Stream _stdin;
    private readonly StringBuilder _stderr = new();
    private readonly int _frameBytes;

    private WebmWriter(Process process, int width, int height)
    {
        _process = process;
        _stdin = process.StandardInput.BaseStream;
        Width = width;
        Height = height;
        _frameBytes = width * height * 4;
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) lock (_stderr) _stderr.AppendLine(e.Data);
        };
        process.BeginErrorReadLine();
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Starts FFmpeg. Width and height must be even (yuv420p). <paramref name="crf"/>: 15 (large, sharp) … 45 (small).</summary>
    public static WebmWriter Start(string ffmpeg, string path, int width, int height, double fps, int crf)
    {
        if ((width & 1) != 0 || (height & 1) != 0) throw new ArgumentException("Frame size must be even.");
        string args = string.Format(CultureInfo.InvariantCulture,
            "-y -hide_banner -loglevel error -f rawvideo -pix_fmt bgra -s {0}x{1} -r {2} -i - -c:v libvpx-vp9 -crf {3} -b:v 0 -row-mt 1 -deadline good -cpu-used 4 -pix_fmt yuv420p \"{4}\"",
            width, height, fps, crf, path);
        var psi = new ProcessStartInfo(ffmpeg, args)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        var p = Process.Start(psi) ?? throw new InvalidOperationException("FFmpeg could not be started.");
        return new WebmWriter(p, width, height);
    }

    public async Task WriteFrameAsync(byte[] bgra)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (bgra.Length != _frameBytes) throw new ArgumentException("Frame size mismatch.");
        try
        {
            await _stdin.WriteAsync(bgra);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("FFmpeg stopped accepting frames: " + Errors(), ex);
        }
    }

    /// <summary>Closes the input and waits for FFmpeg to finish the file.</summary>
    public async Task FinishAsync()
    {
        await _stdin.FlushAsync();
        _stdin.Close();
        await _process.WaitForExitAsync();
        if (_process.ExitCode != 0) throw new InvalidOperationException($"FFmpeg failed ({_process.ExitCode}): {Errors()}");
    }

    private string Errors()
    {
        lock (_stderr) return _stderr.ToString().Trim();
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited) _process.Kill();
        }
        catch (InvalidOperationException)
        {
        }

        _process.Dispose();
    }
}
