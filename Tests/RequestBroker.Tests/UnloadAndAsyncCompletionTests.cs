using System;
using System.Collections.Generic;
using valheimCLI;
using Xunit;

namespace valheimCLI.Tests;

/// <summary>
/// A request still open when the plugin unloads (a live reload) is answered
/// with an explicit error, never an empty success; and an async command that
/// completes inside its own handler gets no extra confirmation line and is not
/// completed twice.
/// </summary>
public class UnloadAndAsyncCompletionTests
{
    private static readonly Action<int> NoSleep = _ => { };

    private static RequestBroker.Request StartAsync(RequestBroker broker, string text)
    {
        RequestBroker.Request request = broker.Submit(text, 30);
        Assert.True(broker.TryDequeue(out RequestBroker.Request running));
        broker.CurrentRequestId = running.Id;
        broker.MarkAsync(running.Id);
        broker.EndHandler(running.Id);
        broker.CurrentRequestId = 0;
        return request;
    }

    [Fact]
    public void APendingAsyncRequestAtShutdownIsAnsweredWithTheUnloadedError()
    {
        RequestBroker broker = new RequestBroker();
        RequestBroker.Request request = StartAsync(broker, "cli_await_plugin MyMod.dll abcdef 30");

        broker.Shutdown(RequestBroker.UnloadedLine);
        RequestBroker.Response response = broker.Wait(request, NoSleep);

        Assert.True(response.Completed);
        Assert.Equal(new[] { RequestBroker.UnloadedLine }, response.Lines);
        Assert.StartsWith("ERROR: code=unloaded ", RequestBroker.UnloadedLine);
        Assert.Equal(0, broker.OpenCount);
    }

    [Fact]
    public void AnAsyncRequestCompletedWithoutOutputIsNeverAnEmptySuccess()
    {
        // The unloading plugin stops the coroutine, whose finally completes the
        // request with nothing said; this used to reach the client as an empty
        // reply with exit code 0.
        RequestBroker broker = new RequestBroker();
        RequestBroker.Request request = StartAsync(broker, "cli_await_plugin MyMod.dll abcdef 30");
        broker.Complete(request.Id);

        RequestBroker.Response response = broker.Wait(request, NoSleep);

        Assert.True(response.Completed);
        Assert.StartsWith("ERROR: code=no_output ", Assert.Single(response.Lines));
    }

    [Fact]
    public void AnEmptyAsyncCompletionDuringShutdownSaysUnloaded()
    {
        RequestBroker broker = new RequestBroker();
        RequestBroker.Request request = StartAsync(broker, "cli_arrive 0 0 0");
        broker.Complete(request.Id);
        broker.Shutdown(RequestBroker.UnloadedLine);

        RequestBroker.Response response = broker.Wait(request, NoSleep);

        Assert.Equal(new[] { RequestBroker.UnloadedLine }, response.Lines);
    }

    [Fact]
    public void AnAsyncAnswerGivenBeforeShutdownIsKept()
    {
        RequestBroker broker = new RequestBroker();
        RequestBroker.Request request = StartAsync(broker, "cli_await_plugin MyMod.dll abcdef 30");
        broker.Output(request.Id, "OK: PLUGIN guid=com.example.mymod");
        broker.Complete(request.Id);
        broker.Shutdown(RequestBroker.UnloadedLine);

        Assert.Equal(new[] { "OK: PLUGIN guid=com.example.mymod" }, broker.Wait(request, NoSleep).Lines);
    }

    [Fact]
    public void AQueuedRequestAtShutdownNeverRunsAndLaterOnesAreAnsweredAtOnce()
    {
        RequestBroker broker = new RequestBroker();
        RequestBroker.Request queued = broker.Submit("spawn Boar", 30);

        broker.Shutdown(RequestBroker.UnloadedLine);
        RequestBroker.Request later = broker.Submit("cli_build", 30);

        Assert.False(broker.TryDequeue(out _));
        Assert.Equal(new[] { RequestBroker.UnloadedLine }, broker.Wait(queued, NoSleep).Lines);
        Assert.Equal(new[] { RequestBroker.UnloadedLine }, broker.Wait(later, NoSleep).Lines);
        Assert.Equal(0, broker.OpenCount);
    }

    [Fact]
    public void AnAsyncCommandThatCompletesInsideItsHandlerGetsNoConfirmationLine()
    {
        RequestBroker broker = new RequestBroker();
        RequestBroker.Request request = broker.Submit("cli_await_plugin com.example.mymod abcdef 30", 30);
        Assert.True(broker.TryDequeue(out RequestBroker.Request running));
        broker.CurrentRequestId = running.Id;

        // Handler: BeginAsync, and the answer is known at once.
        broker.MarkAsync(running.Id);
        broker.Output(running.Id, "OK: PLUGIN guid=com.example.mymod ms=7");
        broker.Complete(running.Id);

        // After the handler, as the plugin does. IsAsync is already false here,
        // which is why asking it added "Executed: ..." after the real answer.
        Assert.False(broker.IsAsync(running.Id));
        Assert.True(broker.BegunAsync(running.Id));
        if (!broker.BegunAsync(running.Id))
            broker.Output("Executed: cli_await_plugin com.example.mymod abcdef 30");
        broker.EndHandler(running.Id);
        broker.CurrentRequestId = 0;

        Assert.Equal(new[] { "OK: PLUGIN guid=com.example.mymod ms=7" }, broker.Wait(request, NoSleep).Lines);
    }

    [Fact]
    public void ASynchronousCommandIsCompletedWhenItsHandlerReturns()
    {
        RequestBroker broker = new RequestBroker();
        RequestBroker.Request request = broker.Submit("cli_build", 30);
        Assert.True(broker.TryDequeue(out RequestBroker.Request running));
        broker.Output(running.Id, "OK: BUILD");
        Assert.False(broker.BegunAsync(running.Id));
        broker.EndHandler(running.Id);

        Assert.True(broker.IsComplete(request.Id));
        Assert.Equal(new[] { "OK: BUILD" }, broker.Wait(request, NoSleep).Lines);
    }

    [Fact]
    public void CompletingARequestWhoseReplyWasTakenLeavesNothingBehind()
    {
        RequestBroker broker = new RequestBroker();
        RequestBroker.Request request = StartAsync(broker, "cli_self_unload");
        broker.Output(request.Id, "OK: UNLOADING");
        broker.Complete(request.Id);
        Assert.Equal(1, broker.OpenCount);
        broker.Wait(request, NoSleep);

        broker.Complete(request.Id);

        Assert.False(broker.IsComplete(request.Id));
        Assert.False(broker.BegunAsync(request.Id));
        Assert.Equal(0, broker.OpenCount);
    }
}
