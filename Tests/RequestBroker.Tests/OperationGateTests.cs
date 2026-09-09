using valheimCLI;
using Xunit;

namespace valheimCLI.Tests;

/// <summary>
/// One owner of the player/camera at a time: a second async command waits,
/// a timed-out owner keeps the gate until it releases (after its in-flight
/// effect settled), and only the owner can release.
/// </summary>
public class OperationGateTests
{
    [Fact]
    public void SecondCommandWaitsUntilTheFirstReleases()
    {
        OperationGate gate = new OperationGate();
        Assert.True(gate.TryAcquire(1, "capture"));
        Assert.False(gate.TryAcquire(2, "capture"));
        Assert.Equal("capture", gate.OwnerName);
        Assert.Equal(1, gate.Owner);
        Assert.True(gate.TryAcquire(1, "capture")); // re-entrant for the owner
        Assert.True(gate.Release(1));
        Assert.True(gate.IsFree);
        Assert.True(gate.TryAcquire(2, "arrive"));
    }

    [Fact]
    public void OnlyTheOwnerCanRelease()
    {
        OperationGate gate = new OperationGate();
        Assert.True(gate.TryAcquire(7, "arrive"));
        Assert.False(gate.Release(8));
        Assert.True(gate.IsOwner(7));
        Assert.False(gate.IsOwner(8));
        Assert.False(gate.IsOwner(0));
        Assert.True(gate.Release(7));
        Assert.False(gate.Release(7));
    }

    [Fact]
    public void TimedOutOwnerHoldsTheGateUntilItsEffectSettled()
    {
        // The broker abandons request 1; the coroutine keeps the gate while the teleport lands,
        // so request 2 (submitted right after the timeout) cannot move the camera under it.
        RequestBroker broker = new RequestBroker();
        OperationGate gate = new OperationGate();
        RequestBroker.Request first = broker.Submit("cli_arrive", 1);
        Assert.True(broker.TryDequeue(out RequestBroker.Request r1));
        broker.MarkAsync(r1.Id);
        Assert.True(gate.TryAcquire(r1.Id, "arrive"));
        System.DateTime now = new System.DateTime(2026, 9, 8, 0, 0, 0, System.DateTimeKind.Utc);
        broker.Wait(first, _ => now = now.AddSeconds(1), () => now);
        Assert.True(broker.IsAbandoned(r1.Id));

        RequestBroker.Request second = broker.Submit("cli_capture", 30);
        Assert.True(broker.TryDequeue(out RequestBroker.Request r2));
        Assert.False(gate.TryAcquire(r2.Id, "capture")); // still settling
        gate.Release(r1.Id);                              // teleport settled
        broker.Complete(r1.Id);
        Assert.True(gate.TryAcquire(r2.Id, "capture"));
    }
}
