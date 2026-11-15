using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Ycs.Properties;

/// <summary>
/// A running interop/properties/oracle.mjs: one script in per line, one line of observations out.
/// Only the live soak and the fixture recorder use it; the recorded tests need no Node.
/// </summary>
internal sealed class Oracle : IDisposable
{
    private readonly Process _process;

    public Oracle()
    {
        var script = Path.Combine(Directory, "oracle.mjs");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("YCS_PROP_NODE") ?? "node")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            UseShellExecute = false,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            WorkingDirectory = Directory,
        };
        start.ArgumentList.Add("--stack-size=4000");
        start.ArgumentList.Add(script);
        _process = Process.Start(start) ?? throw new InvalidOperationException("node did not start");
    }

    /// <summary>interop/properties of the source tree this project was built from (YCS_PROP_INTEROP overrides it).</summary>
    public static string Directory => Environment.GetEnvironmentVariable("YCS_PROP_INTEROP") is { Length: > 0 } d ? d : SourceInterop();

    private static string SourceInterop([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "interop", "properties"));

    public static bool Available => File.Exists(Path.Combine(Directory, "node_modules", "yjs", "package.json"));

    /// <summary>The canonical observations Yjs makes of a script (or {"fatal": ...}).</summary>
    public string Run(string scriptJson)
    {
        _process.StandardInput.WriteLine(scriptJson);
        _process.StandardInput.Flush();
        return _process.StandardOutput.ReadLine() ?? throw new InvalidOperationException("the oracle exited");
    }

    public void Dispose()
    {
        try
        {
            _process.StandardInput.Close();
            if (!_process.WaitForExit(5000))
            {
                _process.Kill();
            }
        }
        finally
        {
            _process.Dispose();
        }
    }
}
