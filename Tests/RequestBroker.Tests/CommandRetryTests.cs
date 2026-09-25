using valheim_cli.Testing;
using Xunit;

namespace valheimCLI.Tests;

/// <summary>
/// --retry-unstarted resends a command only when the game answered that it expired in
/// the queue and never ran. A command that started and timed out, or one whose answer
/// never arrived, may have acted: resending it could act twice, so it is never resent.
/// The expiry lines are the broker's own, so a change there fails these tests.
/// </summary>
public class CommandRetryTests
{
    private static List<string> TimedOutResponse(string command)
    {
        RequestBroker broker = new RequestBroker();
        RequestBroker.Request request = broker.Submit(command, 0);
        return broker.TakeResponse(request.Id).Lines;
    }

    private static List<string> NotStarted() => TimedOutResponse("cli_save");

    private static List<string> StartedAndTimedOut()
    {
        RequestBroker broker = new RequestBroker();
        RequestBroker.Request request = broker.Submit("cli_save", 0);
        Assert.True(broker.TryDequeue(out RequestBroker.Request _));
        return broker.TakeResponse(request.Id).Lines;
    }

    private static List<string> AsyncStartedAndTimedOut()
    {
        RequestBroker broker = new RequestBroker();
        RequestBroker.Request request = broker.Submit("cli_arrive 0 0 0", 0);
        Assert.True(broker.TryDequeue(out RequestBroker.Request _));
        broker.MarkAsync(request.Id);
        return broker.TakeResponse(request.Id).Lines;
    }

    [Fact]
    public void TheBrokersNotStartedExpiryIsNotStarted()
    {
        List<string> output = NotStarted();

        Assert.Contains(output, line => line.Contains(CommandRetry.NotStartedText));
        Assert.Equal(CommandFate.NotStarted, CommandRetry.Classify(output));
    }

    [Fact]
    public void ACommandThatStartedAndTimedOutIsNeverResent()
    {
        Assert.Equal(CommandFate.TimedOutAfterStart, CommandRetry.Classify(StartedAndTimedOut()));
        Assert.Equal(CommandFate.TimedOutAfterStart, CommandRetry.Classify(AsyncStartedAndTimedOut()));
    }

    [Fact]
    public void AMissingAnswerIsNeverResent()
    {
        List<string> output = new() { "ERROR: code=client_timeout message=no response within 125s; the command was not resent (it may have executed); check the server is a valheimCLI with command completion" };

        Assert.Equal(CommandFate.NoAnswer, CommandRetry.Classify(output));
    }

    [Theory]
    [InlineData("OK: SAVE ms=1840 world=TestWorld saveNumber=8 dir=/saves/")]
    [InlineData("ERROR: code=save_failed ms=1840 saveNumber=7 message=...")]
    [InlineData("'nosuch' is not a recognized command. Type 'help' to see a list of valid commands.")]
    public void AnAnswerOfAnyKindIsAnswered(string line)
    {
        Assert.Equal(CommandFate.Answered, CommandRetry.Classify(new List<string> { line }));
        Assert.Equal(CommandFate.Answered, CommandRetry.Classify(new List<string>()));
    }

    [Fact]
    public void AnOrphanLineBeforeTheExpiryDoesNotHideIt()
    {
        List<string> output = new() { "late output of an earlier command" };
        output.AddRange(NotStarted());

        Assert.Equal(CommandFate.NotStarted, CommandRetry.Classify(output));
    }

    [Fact]
    public void NotStartedIsResentUntilItRuns()
    {
        Queue<List<string>> answers = new(new[] { NotStarted(), NotStarted(), new List<string> { "OK: SAVE ms=900" } });
        List<string> retries = new();

        List<string> output = CommandRetry.Send(() => answers.Dequeue(), 3, retries.Add);

        Assert.Equal("OK: SAVE ms=900", Assert.Single(output));
        Assert.Equal(2, retries.Count);
        Assert.StartsWith("RETRY: 1/3 command #1 had not started", retries[0]);
        Assert.StartsWith("RETRY: 2/3 ", retries[1]);
    }

    [Fact]
    public void RetriesStopAtTheLimitWithTheLastAnswer()
    {
        int sends = 0;
        List<string> retries = new();

        List<string> output = CommandRetry.Send(() => { sends++; return NotStarted(); }, 2, retries.Add);

        Assert.Equal(3, sends);
        Assert.Equal(2, retries.Count);
        Assert.Equal(CommandFate.NotStarted, CommandRetry.Classify(output));
    }

    [Fact]
    public void ZeroRetriesSendsOnceAsBefore()
    {
        int sends = 0;

        CommandRetry.Send(() => { sends++; return NotStarted(); }, 0, _ => throw new InvalidOperationException("no heartbeat without a retry"));

        Assert.Equal(1, sends);
    }

    [Fact]
    public void AStartedTimeoutIsReturnedWithoutAResend()
    {
        int sends = 0;

        List<string> output = CommandRetry.Send(() => { sends++; return StartedAndTimedOut(); }, 5, _ => { });

        Assert.Equal(1, sends);
        Assert.Equal(CommandFate.TimedOutAfterStart, CommandRetry.Classify(output));
    }
}
