using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime;

namespace Ycs.Fuzz;

internal enum Verdict
{
    /// <summary>The update applied.</summary>
    Applied,

    /// <summary>The update was rejected with an allowed exception and the document is unchanged.</summary>
    Rejected,

    /// <summary>Did not finish within the time budget.</summary>
    Hang,

    /// <summary>Finished, but took longer than <see cref="Harness.TimeCeiling"/>: an input that is slow for its size.</summary>
    Slow,

    /// <summary>Allocated more than the budget (or ran out of memory).</summary>
    Allocation,

    /// <summary>Threw an exception type outside <see cref="Harness.AllowedExceptionTypes"/>.</summary>
    UnexpectedException,

    /// <summary>Threw, and the document's text, state vector or encoding changed.</summary>
    NonAtomic,

    /// <summary>
    /// Afterwards (applied, rejected or applied in part) the document is not a complete, encodable Yjs document:
    /// its state does not round-trip into a fresh document, or an item list breaks <c>ItemListInvariants</c>.
    /// </summary>
    Inconsistent,
}

/// <summary>The outcome of applying one input.</summary>
internal sealed record Outcome(Verdict Verdict, string? ExceptionType, string? ThrowSite, long AllocatedBytes, TimeSpan Elapsed, string? Detail)
{
    public bool IsViolation => Verdict is not (Verdict.Applied or Verdict.Rejected);

    /// <summary>What identifies a violation across inputs: the verdict, the exception type and where it was thrown.</summary>
    public string Signature => Verdict switch
    {
        Verdict.Hang => "Hang",
        Verdict.Slow => "Slow",
        // Out of memory under the heap limit: the site is where the huge allocation was attempted.
        Verdict.Allocation when ExceptionType == typeof(OutOfMemoryException).FullName => $"Allocation at {ThrowSite}",
        Verdict.Allocation => "Allocation under the heap limit",
        _ => $"{Verdict}: {ExceptionType} at {ThrowSite}",
    };
}

/// <summary>
/// One input: an update to apply, in a format, to a document holding <see cref="BaseState"/>;
/// <see cref="Observed"/>: a document whose every change is read by observers (see <see cref="Harness"/>).
/// </summary>
internal sealed record FuzzInput(bool Gc, bool V2, byte[] BaseState, byte[] Update, IReadOnlyList<string> TextRoots, bool Observed = false)
{
    public static FuzzInput From(Seed seed, byte[] update, bool observed = false) => new(seed.Gc, seed.V2, seed.BaseState, update, seed.TextRoots, observed);
}

/// <summary>
/// Applies one input on a worker thread and checks the properties: it finishes within the time
/// budget, allocates within the allocation budget, throws only an allowed exception type, and a
/// failed apply leaves the document as it was.
/// </summary>
internal sealed class Harness : IDisposable
{
    /// <summary>
    /// The one exception type applying a damaged update may throw: the decoders' own
    /// <see cref="YUpdateException"/>, which leaves the document unchanged.
    /// </summary>
    public static readonly IReadOnlySet<string> AllowedExceptionTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "Ycs.YUpdateException",
    };

    /// <summary>Per input, generous: a valid update of this size applies in well under a millisecond.</summary>
    public static readonly TimeSpan TimeBudget = TimeSpan.FromSeconds(2);

    /// <summary>
    /// What one input may take, applied or rejected. The document's limits (<see cref="Limits"/>) keep
    /// every valid input well below it: the conflict loop to about 10 ms, the structs to about 40 ms.
    /// An input that takes longer is slow for its size (a quadratic path, a limit that does not hold).
    /// </summary>
    public static readonly TimeSpan TimeCeiling = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The fuzzed documents' limits: at most about 1.1 KB per decoded struct (a shared type), so a run
    /// of structs at this limit (a run-length bomb) stays within <see cref="AllocationBudget"/>; and a
    /// conflict loop of at most 2^20 steps.
    /// </summary>
    public static readonly UpdateLimits Limits = new() { MaxStructsPerUpdate = 32_768, MaxIntegrationSteps = 1 << 20 };

    /// <summary>
    /// Per input under 4 KiB; larger inputs get 16 KiB per input byte. The test process runs with a
    /// 512 MiB GC heap limit (runtimeconfig.template.json), so a decoder that trusts a huge length
    /// fails fast with OutOfMemoryException at the allocation instead of taking the machine's memory.
    /// </summary>
    public const long AllocationBudget = 64L << 20;

    private Worker _worker = new();

    public static long AllocationBudgetFor(int inputLength) => Math.Max(AllocationBudget, inputLength * 16L * 1024);

    public Outcome Run(FuzzInput input, TimeSpan? timeBudget = null)
    {
        var budget = timeBudget ?? TimeBudget;
        var task = _worker.Submit(() => Apply(input));
        if (task.Wait(budget))
        {
            var outcome = task.Result;
            if (outcome.AllocatedBytes > AllocationBudget || outcome.Verdict is Verdict.Allocation or Verdict.NonAtomic)
            {
                // Give the memory back before the next input, or it fails for lack of the space this one took.
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                System.GC.Collect();
            }

            return outcome;
        }

        // A thread cannot be aborted: leave the hung one behind (it is a background thread) and start over.
        _worker.Abandon();
        _worker = new Worker();
        return new Outcome(Verdict.Hang, null, null, 0, budget, $"did not finish within {budget.TotalSeconds:0.#} s");
    }

    public void Dispose() => _worker.Abandon();

    private static Outcome Apply(FuzzInput input)
    {
        var doc = Load(input);
        var before = Snapshot.Of(doc, input.TextRoots);
        var allocated0 = System.GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        Exception? failure = null;
        try
        {
            if (input.V2)
            {
                doc.ApplyUpdateV2(input.Update);
            }
            else
            {
                doc.ApplyUpdateV1(input.Update);
            }
        }
        catch (Exception e)
        {
            failure = e;
        }

        stopwatch.Stop();
        var allocated = System.GC.GetAllocatedBytesForCurrentThread() - allocated0;
        var type = failure?.GetType().FullName;
        var site = failure is null ? null : ThrowSite(failure);

        if (allocated > AllocationBudgetFor(input.Update.Length) || failure is OutOfMemoryException)
        {
            var detail = failure is OutOfMemoryException ? "out of memory" : $"{allocated / (1024.0 * 1024.0):F1} MiB";
            return new Outcome(Verdict.Allocation, type, site, allocated, stopwatch.Elapsed, detail);
        }

        if (stopwatch.Elapsed > TimeCeiling)
        {
            return new Outcome(Verdict.Slow, type, site, allocated, stopwatch.Elapsed, $"{stopwatch.Elapsed.TotalMilliseconds:F0} ms");
        }

        if (failure is null)
        {
            return Consistency.Failure(doc) is { } applied
                ? new Outcome(Verdict.Inconsistent, null, null, allocated, stopwatch.Elapsed, applied)
                : new Outcome(Verdict.Applied, null, null, allocated, stopwatch.Elapsed, null);
        }

        if (!AllowedExceptionTypes.Contains(type!))
        {
            return new Outcome(Verdict.UnexpectedException, type, site, allocated, stopwatch.Elapsed, failure.Message);
        }

        // Beyond MaxIntegrationSteps the update stays in part: the document must still be readable.
        var difference = failure is YUpdateException { PartlyApplied: true }
            ? Snapshot.ReadFailure(doc, input.TextRoots)
            : before.DifferenceTo(doc, input.TextRoots);
        if (difference is not null)
        {
            return new Outcome(Verdict.NonAtomic, type, site, allocated, stopwatch.Elapsed, difference);
        }

        return Consistency.Failure(doc) is { } inconsistency
            ? new Outcome(Verdict.Inconsistent, type, site, allocated, stopwatch.Elapsed, inconsistency)
            : new Outcome(Verdict.Rejected, type, site, allocated, stopwatch.Elapsed, null);
    }

    private static YDoc Load(FuzzInput input)
    {
        var doc = new YDoc(new YDocOptions { Gc = input.Gc, ClientId = 1, Limits = Limits });
        if (input.BaseState.Length > 0)
        {
            doc.ApplyUpdateV1(input.BaseState, null, UpdateLimits.Unlimited);
        }

        if (input.Observed)
        {
            Observe(doc, input.TextRoots);
        }

        return doc;
    }

    /// <summary>
    /// What an editor binding does: a document listener (with it every changed type builds its event)
    /// that reads every lazily computed member of every event, the texts' deltas, and an UndoManager on
    /// the texts that captures the remote changes (its default tracked origin is null, as Yjs').
    /// </summary>
    private static void Observe(YDoc doc, IReadOnlyList<string> textRoots)
    {
        var texts = textRoots.Select(doc.GetText).ToList();
        if (texts.Count > 0)
        {
            _ = new UndoManager(texts.Cast<object>().ToList(), new UndoManagerOptions { CaptureTimeout = 0 });
        }

        doc.AfterTransaction += (_, tr) =>
        {
            long sink = 0;
            foreach (var events in tr.ChangedParentTypes.Values)
            {
                foreach (var e in events)
                {
                    sink += e.Path.Count + e.Keys.Count + e.Delta.Count;
                    var changes = e.Changes;
                    sink += (changes.Added?.Count ?? 0) + (changes.Deleted?.Count ?? 0) + (changes.Delta?.Count ?? 0) + (changes.Keys?.Count ?? 0);
                    sink += e switch
                    {
                        YTextEvent text => text.KeysChanged.Count,
                        YXmlEvent xml => xml.AttributesChanged.Count + (xml.ChildListChanged ? 1 : 0),
                        _ => 0,
                    };
                }
            }

            foreach (var text in texts)
            {
                sink += text.ToDelta().Count;
            }

            System.GC.KeepAlive(sink);
        };
    }

    /// <summary>The innermost Ycs method on the stack where the exception was thrown.</summary>
    private static string ThrowSite(Exception e)
    {
        var frames = new StackTrace(e, false).GetFrames();
        var method = frames.Select(f => f.GetMethod()).FirstOrDefault(m => m?.DeclaringType?.Namespace?.StartsWith("Ycs", StringComparison.Ordinal) == true)
            ?? frames.FirstOrDefault()?.GetMethod();
        return method is null ? "?" : $"{method.DeclaringType?.Name}.{method.Name}";
    }

    /// <summary>What a failed apply must leave as it was.</summary>
    private sealed record Snapshot(IReadOnlyList<string> Texts, byte[] StateVector, byte[] Encoded)
    {
        public static Snapshot Of(YDoc doc, IReadOnlyList<string> textRoots) => new(
            textRoots.Select(name => doc.GetText(name).ToString()).ToList(),
            doc.EncodeStateVector(),
            doc.EncodeStateAsUpdateV1());

        /// <summary>Why the document can no longer be read, or null.</summary>
        public static string? ReadFailure(YDoc doc, IReadOnlyList<string> textRoots)
        {
            try
            {
                _ = Of(doc, textRoots);
                return null;
            }
            catch (Exception e)
            {
                return $"reading the document afterwards threw {e.GetType().Name}";
            }
        }

        public string? DifferenceTo(YDoc doc, IReadOnlyList<string> textRoots)
        {
            Snapshot after;
            try
            {
                after = Of(doc, textRoots);
            }
            catch (Exception e)
            {
                return $"reading the document afterwards threw {e.GetType().Name}";
            }

            if (!Texts.SequenceEqual(after.Texts))
            {
                return "text changed";
            }

            if (!StateVector.AsSpan().SequenceEqual(after.StateVector))
            {
                return "state vector changed";
            }

            if (!Encoded.AsSpan().SequenceEqual(after.Encoded))
            {
                return "encoding changed";
            }

            return null;
        }
    }

    /// <summary>A background thread that runs one job at a time.</summary>
    private sealed class Worker
    {
        private readonly BlockingCollection<(Func<Outcome> Job, TaskCompletionSource<Outcome> Result)> _jobs = [];

        public Worker()
        {
            var thread = new Thread(Loop) { IsBackground = true, Name = "fuzz worker" };
            thread.Start();
        }

        public Task<Outcome> Submit(Func<Outcome> job)
        {
            var result = new TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            _jobs.Add((job, result));
            return result.Task;
        }

        public void Abandon() => _jobs.CompleteAdding();

        private void Loop()
        {
            foreach (var (job, result) in _jobs.GetConsumingEnumerable())
            {
                try
                {
                    result.SetResult(job());
                }
                catch (Exception e)
                {
                    result.SetException(e);
                }
            }
        }
    }
}
