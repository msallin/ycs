namespace Ycs.Fuzz;

/// <summary>Shrinks an input while it keeps failing the same way (same <see cref="Outcome.Signature"/>).</summary>
internal static class Minimizer
{
    public static FuzzInput Minimize(Harness harness, FuzzInput input, string signature, int maxRuns)
    {
        var runs = 0;
        bool StillFails(FuzzInput candidate)
        {
            runs++;
            return harness.Run(candidate).Signature == signature;
        }

        if (input.BaseState.Length > 0 && StillFails(input with { BaseState = [] }))
        {
            input = input with { BaseState = [] };
        }

        // Remove chunks, halving the chunk size whenever no chunk of the current size can go; then
        // make the remaining bytes as plain as possible; repeat while either step changes something.
        var data = input.Update;
        var changed = true;
        while (changed && runs < maxRuns)
        {
            changed = false;
            for (var size = Math.Max(1, data.Length / 2); size >= 1 && runs < maxRuns; size /= 2)
            {
                for (var at = 0; at + size <= data.Length && runs < maxRuns;)
                {
                    byte[] candidate = [.. data.AsSpan(0, at), .. data.AsSpan(at + size)];
                    if (StillFails(input with { Update = candidate }))
                    {
                        data = candidate;
                        changed = true;
                    }
                    else
                    {
                        at += size;
                    }
                }
            }

            for (var at = 0; at < data.Length && runs < maxRuns; at++)
            {
                if (data[at] == 0)
                {
                    continue;
                }

                var candidate = (byte[])data.Clone();
                candidate[at] = 0;
                if (StillFails(input with { Update = candidate }))
                {
                    data = candidate;
                    changed = true;
                }
            }
        }

        return input with { Update = data };
    }
}
