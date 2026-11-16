using Ycs.Server.Rooms;

namespace Ycs.Server.Awareness;

/// <summary>
/// Removes the outdated awareness states of every room, as each y-protocols <c>Awareness</c> does on its own timer
/// (every <c>outdatedTimeout / 10</c>, 3 s): one timer for all rooms here.
/// </summary>
internal sealed class AwarenessSweep(RoomRegistry registry, TimeProvider time) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(Awareness.OutdatedTimeoutMs / 10);

    public void SweepOnce()
    {
        foreach (var room in registry.Rooms)
        {
            room.RemoveOutdatedAwareness();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                SweepOnce();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
