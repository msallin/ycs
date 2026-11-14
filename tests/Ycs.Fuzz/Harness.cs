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

    /// <summary>Allocated more than the budget (or ran out of memory).</summary>
    Allocation,

    /// <summary>Threw an exception type outside <see cref="Harness.AllowedExceptionTypes"/>.</summary>
    UnexpectedException,

    /// <summary>Threw, and the document's text, state vector or encoding changed.</summary>
    NonAtomic,
}

/// <summary>The outcome of applying one input.</summary>
internal sealed record Outcome(Verdict Verdict, string? ExceptionType, string? ThrowSite, long AllocatedBytes, TimeSpan Elapsed, string? Detail)
{
    public bool IsViolation => Verdict is not (Verdict.Applied or Verdict.Rejected);

    /// <summary>What identifies a violation across inputs: the verdict, the exception type and where it was thrown.</summary>
    public string Signature => Verdict switch
    {
        Verdict.Hang => "Hang",
        // Out of memory under the heap limit: the site is where the huge allocation was attempted.
        Verdict.Allocation when ExceptionType == typeof(OutOfMemoryException).FullName => $"Allocation at {ThrowSite}",
        Verdict.Allocation => "Allocation under the heap limit",
        _ => $"{Verdict}: {ExceptionType} at {ThrowSite}",
    };
}

/// <summary>One input: an update to apply, in a format, to a document holding <see cref="BaseState"/>.</summary>
internal sealed record FuzzInput(bool Gc, bool V2, byte[] BaseState, byte[] Update, IReadOnlyList<string> TextRoots)
{
    public static FuzzInput From(Seed seed, byte[] update) => new(seed.Gc, seed.V2, seed.BaseState, update, seed.TextRoots);
}

/// <summary>
/// Applies one input on a worker thread and checks the properties: it finishes within the time
/// budget, allocates within the allocation budget, throws only an allowed exception type, and a
/// failed apply leaves the document as it was.
/// </summary>
internal sealed class Harness : IDisposable
{
    /// <summary>
    /// The exception types applying a damaged update throws today, measured by soaks of a few million
    /// inputs. Only the first two come from the decoders' own checks; the rest are whatever the code
    /// throws when it uses a decoded value unchecked. Hardening should shrink this to one type.
    /// </summary>
    public static readonly IReadOnlySet<string> AllowedExceptionTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "System.IO.EndOfStreamException",       // Lib0Reader: input ended early
        "System.IO.InvalidDataException",       // Lib0Reader, AnyEncoding: malformed var-int, unknown 'any' tag
        "System.ArgumentOutOfRangeException",   // a decoded count or offset used unchecked (List capacity, FindIndexSS, Splice, WriteDsLength)
        "System.DivideByZeroException",         // StructStore.FindIndexSS on a struct of length 0
        "System.Exception",                     // StructStore.Find / FindIndexSS / GetItemCleanEnd, Item.GetMissing: "Unexpected"
        "System.InvalidCastException",          // YDocOptions.Read: a subdocument's options of the wrong type
        "System.InvalidOperationException",     // ReadItemContent: unknown content ref; YJson: invalid UTF-8
        "System.NotSupportedException",         // ContentType.Read: unknown type ref
        "System.NullReferenceException",        // YDocOptions.Read: null options
        "System.Text.Json.JsonReaderException", // YJson.Parse / ReadValue: JSON content that is not JSON
    };

    /// <summary>Per input, generous: a valid update of this size applies in well under a millisecond.</summary>
    public static readonly TimeSpan TimeBudget = TimeSpan.FromSeconds(2);

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

        if (failure is null)
        {
            return new Outcome(Verdict.Applied, null, null, allocated, stopwatch.Elapsed, null);
        }

        if (!AllowedExceptionTypes.Contains(type!))
        {
            return new Outcome(Verdict.UnexpectedException, type, site, allocated, stopwatch.Elapsed, failure.Message);
        }

        var difference = before.DifferenceTo(doc, input.TextRoots);
        return difference is null
            ? new Outcome(Verdict.Rejected, type, site, allocated, stopwatch.Elapsed, null)
            : new Outcome(Verdict.NonAtomic, type, site, allocated, stopwatch.Elapsed, difference);
    }

    private static YDoc Load(FuzzInput input)
    {
        var doc = new YDoc(new YDocOptions { Gc = input.Gc, ClientId = 1 });
        if (input.BaseState.Length > 0)
        {
            doc.ApplyUpdateV1(input.BaseState);
        }

        return doc;
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
