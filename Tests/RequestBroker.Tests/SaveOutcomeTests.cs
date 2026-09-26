using Xunit;

namespace valheimCLI.Tests;

/// <summary>
/// cli_save answers when the save thread it started has ended, and calls the save
/// complete only when the game moved its save number on: the thread records nothing
/// else, and a failed save only logs. A script that watched the log for the last save
/// line could miss a save that finished before it started watching.
/// </summary>
public class SaveOutcomeTests
{
    private static SaveOutcome Ended(uint before, uint after)
    {
        return new SaveOutcome
        {
            Started = true,
            Finished = true,
            SaveNumberBefore = before,
            SaveNumberAfter = after,
            Milliseconds = 1840,
            TimeoutSeconds = 120,
            World = "TestWorld",
            Directory = "/saves/worlds_local/TestWorld/"
        };
    }

    [Fact]
    public void ASaveThatMovedTheSaveNumberOnIsComplete()
    {
        SaveOutcome outcome = Ended(7, 8);

        Assert.True(outcome.Saved);
        Assert.Equal("OK: SAVE ms=1840 world=TestWorld saveNumber=8 dir=/saves/worlds_local/TestWorld/", outcome.Reply());
    }

    [Fact]
    public void ASaveThatEndedWithTheSameSaveNumberFailed()
    {
        SaveOutcome outcome = Ended(7, 7);

        Assert.False(outcome.Saved);
        Assert.StartsWith("ERROR: code=save_failed ms=1840 saveNumber=7 ", outcome.Reply());
    }

    [Fact]
    public void ASaveStillWritingAtTheDeadlineTimesOut()
    {
        SaveOutcome outcome = Ended(7, 7);
        outcome.Finished = false;

        Assert.False(outcome.Saved);
        Assert.StartsWith("ERROR: code=save_timeout ms=1840 saveNumber=7 ", outcome.Reply());
        Assert.Contains("after 120s", outcome.Reply());
    }

    [Fact]
    public void ASaveThatNeverStartedWroteNothing()
    {
        SaveOutcome outcome = Ended(7, 7);
        outcome.Started = false;
        outcome.Finished = false;

        Assert.False(outcome.Saved);
        Assert.StartsWith("ERROR: code=save_skipped reason=not_started ", outcome.Reply());
    }

    [Fact]
    public void ASkippedSaveNamesItsReason()
    {
        SaveOutcome outcome = new SaveOutcome { Skipped = "load_error" };

        Assert.False(outcome.Saved);
        Assert.StartsWith("ERROR: code=save_skipped reason=load_error ", outcome.Reply());
    }

    [Theory]
    [InlineData(true, true, true, true, false, "session_flag")]
    [InlineData(false, true, true, true, false, "load_error")]
    [InlineData(false, false, true, true, false, "zone_system")]
    [InlineData(false, false, false, true, false, "dungeon_db")]
    [InlineData(false, false, false, false, false, "low_disk")]
    [InlineData(false, false, false, false, true, "")]
    public void SkipReasonsFollowTheGamesOrder(bool session, bool loadError, bool zones, bool dungeons, bool disk, string reason)
    {
        Assert.Equal(reason, SaveOutcome.SkipReason(session, loadError, zones, dungeons, disk));
    }
}
