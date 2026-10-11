using System.IO;
using System.IO.Pipes;
using System.Text;

namespace ImpressionFEA.App.Infrastructure;

/// <summary>
/// One ImpressionFEA window per user for jobs sent from CAD (the SolidWorks add-in): a second launch forwards its
/// command line to the running instance over a named pipe and exits; the running instance handles it in place.
/// </summary>
public static class SingleInstance
{
    private static readonly string PipeName = "ImpressionFEA-" + Environment.UserName;

    /// <summary>True when an instance is already listening and accepted <paramref name="args"/>.</summary>
    public static bool TryForward(IReadOnlyList<string> args)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(400);
            var bytes = Encoding.UTF8.GetBytes(string.Join("\0", args));
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Listens for forwarded command lines and passes each one to <paramref name="handle"/> (on a background thread).</summary>
    public static void Listen(Action<string[]> handle, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handle);
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(ct);
                    using var ms = new MemoryStream();
                    await server.CopyToAsync(ms, ct);
                    var text = Encoding.UTF8.GetString(ms.ToArray());
                    if (text.Length > 0) handle(text.Split('\0'));
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException)
                {
                    await Task.Delay(200, CancellationToken.None);
                }
            }
        }, ct);
    }
}
