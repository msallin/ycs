using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Ycs.Client.Tests.Support;

/// <summary>
/// The sync server (<c>samples/Ycs.Server</c>, built with the tests) as a child process on a free loopback port: its
/// types are internal to it and its own tests, so it runs as it ships.
/// </summary>
internal sealed partial class ServerProcess : IDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    private readonly Process process;

    private ServerProcess(Process process, int port)
    {
        this.process = process;
        Port = port;
    }

    public int Port { get; }

    /// <summary>The URL a provider is given: <c>ws://127.0.0.1:port/ws</c>.</summary>
    public Uri WebSocketBase => new($"ws://127.0.0.1:{Port}/ws");

    public static async Task<ServerProcess> StartAsync()
    {
        var assembly = typeof(ServerProcess).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "ServerAssembly").Value!;
        var start = new ProcessStartInfo("dotnet")
        {
            ArgumentList =
            {
                assembly, "--urls", "http://127.0.0.1:0", "--Logging:LogLevel:Default", "Warning",
                "--Logging:LogLevel:Microsoft.Hosting.Lifetime", "Information", "--Logging:Console:ColorBehavior", "Disabled",
            },
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        var process = Process.Start(start)!;
        var listening = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                listening.TrySetException(new InvalidOperationException("The server exited before it listened."));
            }
            else if (Listening().Match(e.Data) is { Success: true } match)
            {
                listening.TrySetResult(int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
            }
        };
        process.BeginOutputReadLine();
        try
        {
            return new ServerProcess(process, await listening.Task.WaitAsync(StartTimeout));
        }
        catch
        {
            process.Kill(entireProcessTree: true);
            process.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        process.Kill(entireProcessTree: true);
        process.WaitForExit();
        process.Dispose();
    }

    [GeneratedRegex(@"Now listening on: http://127\.0\.0\.1:(\d+)")]
    private static partial Regex Listening();
}
