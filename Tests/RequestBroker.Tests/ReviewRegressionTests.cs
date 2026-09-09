using System;
using valheimCLI;
using Xunit;

namespace valheimCLI.Tests;

/// <summary>Regression from the 8 Sep 2026 review: a queued request whose caller gave up must never run.</summary>
public class ReviewRegressionTests
{
    [Fact]
    public void QueuedCommandThatExpiresMustNotExecuteLater()
    {
        RequestBroker broker = new RequestBroker();
        RequestBroker.Request running = broker.Submit("road_generate", 120);
        Assert.True(broker.TryDequeue(out _));
        RequestBroker.Request queued = broker.Submit("spawn Troll", 1);
        DateTime now = new DateTime(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc);
        RequestBroker.Response response = broker.Wait(queued, _ => now = now.AddSeconds(1), () => now);
        Assert.False(response.Completed);
        Assert.True(broker.IsAbandoned(queued.Id));
        Assert.Contains(response.Lines, l => l.Contains("had not started and will not run"));
        broker.Complete(running.Id);
        Assert.False(broker.TryDequeue(out _));
        Assert.Equal(1, broker.ExpiredSkipped);
    }

    [Fact]
    public void RunningCommandKeepsRunningAndTheQueuedOneBehindItExpires()
    {
        RequestBroker broker = new RequestBroker();
        RequestBroker.Request slow = broker.Submit("road_generate", 1);
        RequestBroker.Request behind = broker.Submit("cli_capture x", 1);
        Assert.True(broker.TryDequeue(out RequestBroker.Request first));
        Assert.Equal(slow.Id, first.Id);
        Assert.True(broker.IsRunning(slow.Id));
        DateTime now = new DateTime(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc);
        RequestBroker.Response slowResponse = broker.Wait(slow, _ => now = now.AddSeconds(1), () => now);
        RequestBroker.Response behindResponse = broker.Wait(behind, _ => now = now.AddSeconds(1), () => now);
        Assert.Contains(slowResponse.Lines, l => l.Contains("still runs on the game thread"));
        Assert.Contains(behindResponse.Lines, l => l.Contains("had not started and will not run"));
        broker.Complete(slow.Id);
        Assert.False(broker.TryDequeue(out _));
    }
}
