using System;
using System.Threading;
using System.Threading.Tasks;
using valheimCLI;
using Xunit;

namespace valheimCLI.Tests;

public sealed class MainThreadHeartbeatTests
{
    [Fact]
    public async Task StatusRemainsReadableAndShowsAStallWhileTheGameThreadIsPaused()
    {
        long ticks = 0;
        MainThreadHeartbeat heartbeat = new(() => Interlocked.Read(ref ticks), 1000);
        RequestBroker broker = new();
        Assert.Contains("mainThreadIdleMs=-1", heartbeat.StatusFields(broker));

        heartbeat.Stamp();
        RequestBroker.Request request = broker.Submit("road_generate", 30);
        Interlocked.Exchange(ref ticks, 37_000);
        // This worker stands in for the socket thread; no game-thread Update or dequeue occurs.
        string status = await Task.Run(() => heartbeat.StatusFields(broker));
        Assert.Contains("mainThreadIdleMs=37000", status);
        Assert.Contains("queued=1", status);
        Assert.Contains("running=none", status);
        Assert.Contains("runningMs=0", status);
        Assert.False(broker.IsRunning(request.Id));
    }

    [Fact]
    public void StatusReportsTheExecutingRequestAndOmitsAnAbandonedQueueEntry()
    {
        long ticks = 100;
        MainThreadHeartbeat heartbeat = new(() => ticks, 1000);
        RequestBroker broker = new();
        heartbeat.Stamp();
        RequestBroker.Request running = broker.Submit("slow", 120);
        Assert.True(broker.TryDequeue(out RequestBroker.Request dequeued));
        broker.CurrentRequestId = dequeued.Id;
        RequestBroker.Request waiting = broker.Submit("never-run", 0);
        broker.Wait(waiting, _ => { }); // The command expired without starting.
        string status = heartbeat.StatusFields(broker);
        Assert.Contains("queued=0", status);
        Assert.Contains("running=" + running.Id, status);
        Assert.Matches(@"runningMs=\d+", status);
        broker.Complete(running.Id);
        broker.CurrentRequestId = 0;
        Assert.Contains("running=none", heartbeat.StatusFields(broker));
    }

    [Fact]
    public void BusyNoteIsBoundedEncodedAndClearedWithoutAFrame()
    {
        long ticks = 100;
        MainThreadHeartbeat heartbeat = new(() => ticks, 1000);
        RequestBroker broker = new();
        heartbeat.Stamp();
        heartbeat.SetBusy("generating roads 12/26 islands");
        ticks = 4_100;
        string status = heartbeat.StatusFields(broker);
        Assert.Contains("busy=generating%20roads%2012%2F26%20islands", status);
        Assert.Contains("busyMs=4000", status);
        Assert.Contains("mainThreadIdleMs=4000", status);
        Assert.Throws<ArgumentException>(() => heartbeat.SetBusy("bad\nline"));
        Assert.Throws<ArgumentException>(() => heartbeat.SetBusy(new string('x', 121)));
        heartbeat.ClearBusy();
        Assert.Contains("busy=none busyMs=0", heartbeat.StatusFields(broker));
    }
}
