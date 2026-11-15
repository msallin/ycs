namespace Ycs.Compare;

/// <summary>
/// One run of a workload on one library: everything before <see cref="Body"/> is set up and not timed;
/// <see cref="Body"/> is timed; <see cref="Content"/> and <see cref="StateV1"/> are read afterwards for the checks.
/// </summary>
internal sealed class Prepared(Action body, Func<string> content, Func<byte[]> stateV1, Func<byte[]> stateV2, Action dispose = null) : IDisposable
{
    public Action Body { get; } = body;

    /// <summary>The canonical content (text, sorted map entries, array values, or what a read path read).</summary>
    public Func<string> Content { get; } = content;

    public Func<byte[]> StateV1 { get; } = stateV1;

    public Func<byte[]> StateV2 { get; } = stateV2;

    public void Dispose() => dispose?.Invoke();
}

/// <summary>What a child process reads: the editing trace and the updates the prepare step wrote.</summary>
internal sealed class Fixtures(string dir)
{
    private readonly Lazy<Trace> _trace = new(() => Inputs.LoadTrace(Path.Combine(dir, "trace.json.gz")));

    public string Dir { get; } = dir;

    public Trace Trace => _trace.Value;

    /// <summary>A file of the prepare step, e.g. <c>trace.ycs.v1</c>.</summary>
    public byte[] Bytes(string name) => File.ReadAllBytes(Path.Combine(Dir, name));

    public byte[][] Updates(string name) => Inputs.ReadUpdates(Path.Combine(Dir, name));
}
