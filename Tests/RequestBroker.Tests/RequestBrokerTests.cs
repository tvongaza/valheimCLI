using System;
using System.Collections.Generic;
using valheimCLI;
using Xunit;

namespace valheimCLI.Tests;

/// <summary>
/// The request/response pairing behind the CLI server: a response carries the
/// whole output of its own command, an async command completes later, a timed
/// out request is abandoned and its late output dropped, callback output with
/// no owner rides along with the next response.
/// </summary>
public class RequestBrokerTests
{
    /// <summary>Plays the game thread: dequeue, run, complete.</summary>
    private static void RunOne(RequestBroker broker, Action<RequestBroker.Request> handler)
    {
        Assert.True(broker.TryDequeue(out RequestBroker.Request request));
        broker.CurrentRequestId = request.Id;
        handler(request);
        if (!broker.IsAsync(request.Id))
            broker.Complete(request.Id);
        broker.CurrentRequestId = 0;
    }

    private static readonly Action<int> NoSleep = _ => { };

    [Fact]
    public void ResponseHoldsTheWholeOutputOfItsOwnCommand()
    {
        RequestBroker broker = new RequestBroker();
        RequestBroker.Request request = broker.Submit("road_generate", 30);
        RunOne(broker, r =>
        {
            broker.Output("Generating...");
            broker.Output("Total road points: 20626");
        });

        RequestBroker.Response response = broker.Wait(request, NoSleep);

        Assert.True(response.Completed);
        Assert.Equal(new[] { "Generating...", "Total road points: 20626" }, response.Lines);
        Assert.Equal(0, broker.PendingCount);
    }

    [Fact]
    public void RequestsAreServedInOrderAndKeepTheirOwnOutput()
    {
        RequestBroker broker = new RequestBroker();
        RequestBroker.Request a = broker.Submit("a", 30);
        RequestBroker.Request b = broker.Submit("b", 30);
        RunOne(broker, r => broker.Output("out-a"));
        RunOne(broker, r => broker.Output("out-b"));

        Assert.Equal(new[] { "out-b" }, broker.Wait(b, NoSleep).Lines);
        Assert.Equal(new[] { "out-a" }, broker.Wait(a, NoSleep).Lines);
    }

    [Fact]
    public void AsyncCommandCompletesLaterAndTheWaitFollowsIt()
    {
        RequestBroker broker = new RequestBroker();
        RequestBroker.Request request = broker.Submit("cli_capture shot", 30);
        long id = 0;
        RunOne(broker, r => { id = r.Id; broker.MarkAsync(r.Id); broker.Output("posed"); });
        Assert.False(broker.IsComplete(id));

        // The socket thread is polling; the coroutine finishes on the third poll.
        int polls = 0;
        RequestBroker.Response response = broker.Wait(request, _ =>
        {
            if (++polls == 3)
            {
                broker.Output(id, "OK: CAPTURE bytes=1234");
                broker.Complete(id);
            }
        });

        Assert.True(response.Completed);
        Assert.Equal(new[] { "posed", "OK: CAPTURE bytes=1234" }, response.Lines);
        Assert.Equal(3, polls);
    }

    [Fact]
    public void TimedOutRequestIsAbandonedAndItsLateOutputIsDroppedAndNoted()
    {
        RequestBroker broker = new RequestBroker();
        RequestBroker.Request slow = broker.Submit("road_generate", 1);
        long id = 0;
        RunOne(broker, r => { id = r.Id; broker.MarkAsync(r.Id); });

        DateTime t = new DateTime(2026, 9, 8, 12, 0, 0, DateTimeKind.Utc);
        RequestBroker.Response response = broker.Wait(slow, _ => t = t.AddSeconds(1), () => t);

        Assert.False(response.Completed);
        Assert.Contains(response.Lines, l => l.StartsWith("ERROR: code=command_timeout") && l.Contains("cancelled"));
        Assert.True(broker.IsAbandoned(id));
        Assert.False(broker.IsAsync(id));

        // Output arriving now belongs to nobody: dropped, and the next response says so.
        broker.Output(id, "Total road points: 20626");
        broker.Complete(id);
        RequestBroker.Request next = broker.Submit("road_timings", 30);
        RunOne(broker, r => broker.Output("run=x"));
        RequestBroker.Response nextResponse = broker.Wait(next, NoSleep);

        Assert.Equal("run=x", nextResponse.Lines[0]);
        Assert.Contains(nextResponse.Lines, l => l.StartsWith("NOTE: dropped 1 late output line(s) from request #" + id));
        Assert.DoesNotContain(nextResponse.Lines, l => l.Contains("20626"));
    }

    [Fact]
    public void TimedOutSyncCommandIsReportedAsStillRunning()
    {
        RequestBroker broker = new RequestBroker();
        RequestBroker.Request slow = broker.Submit("road_generate", 1);
        Assert.True(broker.TryDequeue(out RequestBroker.Request request));
        broker.CurrentRequestId = request.Id;
        // the game thread is still inside the handler when the socket thread gives up
        DateTime t = new DateTime(2026, 9, 8, 12, 0, 0, DateTimeKind.Utc);
        RequestBroker.Response response = broker.Wait(slow, _ => t = t.AddSeconds(1), () => t);
        Assert.False(response.Completed);
        Assert.Contains(response.Lines, l => l.Contains("still runs on the game thread"));
        broker.Output("Total road points: 20626");
        broker.Complete(request.Id);
        broker.CurrentRequestId = 0;
        RequestBroker.Request next = broker.Submit("pos", 30);
        Assert.True(broker.TryDequeue(out RequestBroker.Request n2));
        broker.CurrentRequestId = n2.Id; broker.Output("x=1"); broker.Complete(n2.Id); broker.CurrentRequestId = 0;
        RequestBroker.Response nextResponse = broker.Wait(next, NoSleep);
        Assert.Equal("x=1", nextResponse.Lines[0]);
        Assert.Contains(nextResponse.Lines, l => l.StartsWith("NOTE: dropped 1 late output line(s)"));
    }

    [Fact]
    public void OwnerlessOutputRidesWithTheNextResponse()
    {
        RequestBroker broker = new RequestBroker();
        // A callback firing after its command completed (no current request).
        broker.Output("OK: joined server");
        RequestBroker.Request request = broker.Submit("pos", 30);
        RunOne(broker, r => broker.Output("x=1"));

        RequestBroker.Response response = broker.Wait(request, NoSleep);

        Assert.Equal(new[] { "OK: joined server", "x=1" }, response.Lines);
    }

    [Fact]
    public void CommandWithNoOutputStillCompletesWithAnEmptyResponse()
    {
        RequestBroker broker = new RequestBroker();
        RequestBroker.Request request = broker.Submit("silent", 30);
        RunOne(broker, r => { });

        RequestBroker.Response response = broker.Wait(request, NoSleep);

        Assert.True(response.Completed);
        Assert.Empty(response.Lines);
    }
}
