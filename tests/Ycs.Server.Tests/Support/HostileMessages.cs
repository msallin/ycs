using Ycs.Server.Rooms;

namespace Ycs.Server.Tests.Support;

/// <summary>Malformed and hostile y-websocket messages: mutations of valid ones, and awareness garbage.</summary>
internal static class HostileMessages
{
    private const ulong MaxSafeInteger = (1UL << 53) - 1;

    /// <summary>A sync message of the given sub-type around <paramref name="payload"/>, its length as given.</summary>
    public static byte[] Sync(ulong syncType, byte[] payload, ulong? length = null)
    {
        var stream = new MemoryStream();
        HostileUpdates.WriteVarUint(stream, MessageType.Sync);
        HostileUpdates.WriteVarUint(stream, syncType);
        HostileUpdates.WriteVarUint(stream, length ?? (ulong)payload.Length);
        stream.Write(payload);
        return stream.ToArray();
    }

    /// <summary>
    /// One mutation of a sync message carrying <paramref name="payload"/> (an update or a state vector); returns
    /// the message and the mutation's name.
    /// </summary>
    public static (byte[] Message, string Kind) Mutate(HostileRandom rng, ulong syncType, byte[] payload)
    {
        var valid = Sync(syncType, payload);
        switch (rng.Int(9))
        {
            case 0:
                {
                    var flipped = (byte[])valid.Clone();
                    var flips = rng.Between(1, 3);
                    for (var i = 0; i < flips; i++)
                    {
                        flipped[rng.Int(flipped.Length)] ^= (byte)(1 << rng.Int(8));
                    }

                    return (flipped, "bit flip");
                }

            case 1:
                return (valid[..rng.Int(valid.Length)], "truncated");
            case 2:
                return ([.. valid, .. rng.Bytes(rng.Between(1, 16))], "extended");
            case 3:
                return (Sync(syncType, payload, (ulong)rng.Int(payload.Length)), "length shorter");
            case 4:
                return (Sync(syncType, payload, (ulong)(payload.Length + rng.Between(1, 1_000))), "length longer");
            case 5:
                return (Sync(syncType, payload, rng.Pick<ulong>([uint.MaxValue, int.MaxValue, 1UL << 31, 1UL << 32, MaxSafeInteger])), "length huge");
            case 6:
                {
                    var stream = new MemoryStream();
                    HostileUpdates.WriteVarUint(stream, rng.Pick<ulong>([2, 4, 5, 127, 128, uint.MaxValue, 1UL << 35]));
                    stream.Write(valid.AsSpan(1));
                    return (stream.ToArray(), "outer type");
                }

            case 7:
                return (Sync(rng.Pick<ulong>([3, 4, 127, 128, uint.MaxValue, 1UL << 35]), payload), "sync type");
            default:
                return (rng.Bytes(rng.Int(32)), "random bytes");
        }
    }

    /// <summary>An awareness message of several entries, in the given order; a null state removes the client.</summary>
    public static byte[] Awareness(params (ulong ClientId, ulong Clock, string? Json)[] entries)
    {
        var update = new MemoryStream();
        HostileUpdates.WriteVarUint(update, (ulong)entries.Length);
        foreach (var (clientId, clock, json) in entries)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(json ?? "null");
            HostileUpdates.WriteVarUint(update, clientId);
            HostileUpdates.WriteVarUint(update, clock);
            HostileUpdates.WriteVarUint(update, (ulong)bytes.Length);
            update.Write(bytes);
        }

        var message = new MemoryStream();
        HostileUpdates.WriteVarUint(message, MessageType.Awareness);
        HostileUpdates.WriteVarUint(message, (ulong)update.Length);
        update.WriteTo(message);
        return message.ToArray();
    }

    /// <summary>An awareness message with garbage in it; returns the message and what it holds.</summary>
    public static (byte[] Message, string Kind) Awareness(HostileRandom rng, IReadOnlyList<ulong> knownIds)
    {
        var kind = rng.Int(8);
        var update = new MemoryStream();
        var count = kind == 1 ? rng.Between(50, 400) : rng.Between(1, 4);
        var lyingCount = kind == 2;
        HostileUpdates.WriteVarUint(update, lyingCount ? (ulong)count + (ulong)rng.Between(1, 1_000) : (ulong)count);
        for (var i = 0; i < count; i++)
        {
            var id = kind switch
            {
                3 when knownIds.Count > 0 => rng.Pick(knownIds),
                4 => rng.Pick([MaxSafeInteger, MaxSafeInteger + 1, ulong.MaxValue >> 1, 1UL << 32]),
                _ => (ulong)rng.Between(1, 64),
            };
            var clock = kind == 5 ? rng.Pick([MaxSafeInteger, MaxSafeInteger + 1, 0UL]) : (ulong)rng.Between(0, 40);
            var json = kind switch
            {
                6 => System.Text.Encoding.UTF8.GetBytes(rng.Pick(["{", "nul", "{\"a\":}", "\"", "[1,]", "", "{} {}", "NaN"])),
                7 => rng.Chance(0.5) ? [0xC3, 0x28] : rng.Bytes(rng.Between(1, 8)),
                _ => System.Text.Encoding.UTF8.GetBytes(rng.Chance(0.3) ? "null" : $"{{\"n\":{rng.Int(1_000)}}}"),
            };
            HostileUpdates.WriteVarUint(update, id);
            HostileUpdates.WriteVarUint(update, clock);
            HostileUpdates.WriteVarUint(update, kind == 0 && rng.Chance(0.5) ? (ulong)json.Length + 7 : (ulong)json.Length);
            update.Write(json);
        }

        var message = new MemoryStream();
        HostileUpdates.WriteVarUint(message, MessageType.Awareness);
        HostileUpdates.WriteVarUint(message, kind == 0 && rng.Chance(0.5) ? (ulong)update.Length + 3 : (ulong)update.Length);
        update.WriteTo(message);
        var name = kind switch
        {
            0 => "awareness, lying lengths",
            1 => "awareness, many clients",
            2 => "awareness, lying count",
            3 => "awareness, others' ids",
            4 => "awareness, huge ids",
            5 => "awareness, huge clocks",
            6 => "awareness, invalid JSON",
            _ => "awareness, invalid UTF-8",
        };
        return (message.ToArray(), name);
    }
}
