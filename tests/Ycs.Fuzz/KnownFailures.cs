using System.Text.Json;

namespace Ycs.Fuzz;

/// <summary>A minimal input that violates a property today, kept until the bug is fixed.</summary>
internal sealed record PinnedCase(string Name, string Signature, string Reason, bool Gc, bool V2, string BaseState, string Update, string[] TextRoots)
{
    public FuzzInput Input => new(Gc, V2, Convert.FromBase64String(BaseState), Convert.FromBase64String(Update), TextRoots);
}

/// <summary>
/// Property violations the fuzzer found and that are not fixed (src/ is not changed by the fuzz work).
/// <see cref="FuzzTests.MutatedUpdates_FailCleanly"/> tolerates a violation whose signature is listed
/// here; <see cref="FuzzTests.PinnedCase_FailsCleanly"/> reports each entry as Inconclusive while it
/// still fails and fails the run once it passes, so the entry gets removed with the fix.
/// </summary>
internal static class KnownFailures
{
    public static readonly IReadOnlyList<PinnedCase> All = [];


    /// <summary>An input as the C# of a <see cref="PinnedCase"/>, ready to paste.</summary>
    public static string Describe(FuzzInput input) =>
        $"Gc: {(input.Gc ? "true" : "false")}, V2: {(input.V2 ? "true" : "false")}, " +
        $"BaseState: \"{Convert.ToBase64String(input.BaseState)}\", Update: \"{Convert.ToBase64String(input.Update)}\", " +
        $"TextRoots: {JsonSerializer.Serialize(input.TextRoots)}";
}
