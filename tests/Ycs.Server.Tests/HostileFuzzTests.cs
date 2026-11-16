using System.Globalization;
using Ycs.Server.Tests.Support;

namespace Ycs.Server.Tests;

/// <summary>
/// Honest clients edit through a room while hostile sessions send mutated, replayed, out-of-order, expensive and
/// awareness garbage messages (<see cref="HostileRoom"/>); after every step no exception has escaped, only hostile
/// sessions have closed, every honest client holds what the room broadcast, the room holds that plus what waits
/// (bounded), and awareness shows no state that no open session set.
/// </summary>
/// <remarks><c>YCS_SERVER_FUZZ=first:count</c> runs other seeds.</remarks>
[TestClass]
public class HostileFuzzTests
{
    private const int Steps = 100;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Run_RandomHostileSessions_HonestClientsConvergeWithTheRoom()
    {
        var (first, count) = Seeds();
        var failures = new List<(ulong Seed, string Message)>();
        var stats = new HostileStats();

        // Seeds are independent; a long run uses half the cores.
        Parallel.For((long)first, (long)(first + count), new ParallelOptions { MaxDegreeOfParallelism = count > 50 ? Math.Max(1, Environment.ProcessorCount / 2) : 1 }, (seed, state) =>
        {
            var room = new HostileRoom((ulong)seed);
            try
            {
                room.Run(Steps);
            }
            catch (HostileRoomException e)
            {
                lock (failures)
                {
                    failures.Add(((ulong)seed, e.Message + (e.InnerException is { } inner ? $"\n{inner}" : "")));
                    if (failures.Count >= 5)
                    {
                        state.Stop();
                    }
                }
            }

            lock (stats)
            {
                stats.Add(room.Stats);
            }
        });

        TestContext.WriteLine($"seeds {first}..{first + count - 1}: {stats}");
        Assert.AreEqual(0, failures.Count, string.Join("\n\n", failures.OrderBy(f => f.Seed).Select(f => f.Message)));
    }

    private static (ulong First, ulong Count) Seeds()
    {
        var spec = Environment.GetEnvironmentVariable("YCS_SERVER_FUZZ");
        if (string.IsNullOrEmpty(spec))
        {
            return (1, 10);
        }

        var parts = spec.Split(':');
        return (ulong.Parse(parts[0], CultureInfo.InvariantCulture), ulong.Parse(parts[1], CultureInfo.InvariantCulture));
    }
}
