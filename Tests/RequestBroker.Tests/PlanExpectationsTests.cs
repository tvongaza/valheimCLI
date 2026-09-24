using valheim_cli.Testing;
using Xunit;

namespace valheimCLI.Tests;

/// <summary>
/// Expectations in a test plan run: which file and mode apply (command line or
/// the plan's game keys), reading the file, judging the game's cli_expect reply,
/// and the run's exit code.
/// </summary>
public class PlanExpectationsTests
{
    private static readonly string PlanPath = Path.Combine("plans", "smoke.yaml");

    [Fact]
    public void NoFileAnywhereMeansNoCheck()
    {
        Assert.Null(PlanExpectations.Resolve(null, false, "", false, PlanPath));
        Assert.Null(PlanExpectations.Resolve(null, false, null, true, PlanPath));
    }

    [Fact]
    public void TheCommandLineWinsOverThePlanFileAndMode()
    {
        ExpectationSource strict = PlanExpectations.Resolve("ci.pins", true, "pins.txt", false, PlanPath)!;
        Assert.Equal("ci.pins", strict.Path);
        Assert.True(strict.Strict);
        Assert.Equal("--expect-strict", strict.From);

        // --expect is not strict even when the plan asks for strict: the caller chose the mode.
        ExpectationSource loose = PlanExpectations.Resolve("ci.pins", false, "pins.txt", true, PlanPath)!;
        Assert.False(loose.Strict);
        Assert.Equal("--expect", loose.From);
    }

    [Fact]
    public void ThePlanFileIsRelativeToThePlanAndKeepsItsMode()
    {
        ExpectationSource source = PlanExpectations.Resolve(null, false, "pins.txt", true, PlanPath)!;
        Assert.Equal(Path.Combine("plans", "pins.txt"), source.Path);
        Assert.True(source.Strict);
        Assert.Equal("plan", source.From);

        string rooted = Path.Combine(Path.GetTempPath(), "pins.txt");
        Assert.Equal(rooted, PlanExpectations.Resolve(null, false, rooted, false, PlanPath)!.Path);
    }

    [Fact]
    public void ThePlanKeysAreReadFromTheGameSection()
    {
        TestPlan plan = TestPlan.Parse("name: p\ngame:\n  launch: true\n  expect: pins.txt\n  expectStrict: true\ntests: []\n");
        Assert.Equal("pins.txt", plan.Game.Expect);
        Assert.True(plan.Game.ExpectStrict);

        TestPlan without = TestPlan.Parse("name: p\ntests: []\n");
        Assert.Equal("", without.Game.Expect);
        Assert.False(without.Game.ExpectStrict);
    }

    [Fact]
    public void AStepExpectIsNotMistakenForThePlanKey()
    {
        TestPlan plan = TestPlan.Parse("name: p\ntests:\n  - name: s\n    commands: [help]\n    expect:\n      output: 'contains \"x\"'\n");
        Assert.Equal("", plan.Game.Expect);
        Assert.NotNull(plan.Tests[0].Expect);
    }

    [Fact]
    public void AFileBecomesTheCliExpectCommandInItsMode()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "# pins\ncom.example.mymod=5f2b0c6e   # 1.4.0\nworld=Dev\n");
            Assert.True(PlanExpectations.TryLoad(path, true, out string command, out string error), error);
            Assert.Equal("cli_expect --strict com.example.mymod=5f2b0c6e world=Dev", command);
            Assert.True(PlanExpectations.TryLoad(path, false, out command, out _));
            Assert.Equal("cli_expect com.example.mymod=5f2b0c6e world=Dev", command);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AMissingMalformedOrEmptyFileIsNotLoaded()
    {
        Assert.False(PlanExpectations.TryLoad(Path.Combine(Path.GetTempPath(), "no-such-pins-file.txt"), false, out _, out string missing));
        Assert.Contains("not found", missing);

        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "com.example.mymod=abc\n");
            Assert.False(PlanExpectations.TryLoad(path, false, out _, out string malformed));
            Assert.Contains("line 1", malformed);

            File.WriteAllText(path, "# nothing yet\n\n");
            Assert.False(PlanExpectations.TryLoad(path, false, out _, out string empty));
            Assert.Contains("no expectations", empty);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static readonly ExpectationSource Pins = new ExpectationSource { Path = "pins.txt", Strict = true, From = "--expect-strict" };

    [Fact]
    public void TheGameMustSayTheExpectationsHold()
    {
        ExpectationCheck held = PlanExpectations.Judge(Pins, new List<string> { "OK: EXPECT 2 expectation(s) met (strict)" });
        Assert.True(held.Held);
        Assert.Equal("held (pins.txt, strict, from --expect-strict)", held.Summary());
    }

    [Fact]
    public void MismatchLinesFailAndAreKept()
    {
        List<string> reply = new List<string>
        {
            "MISMATCH com.example.mymod: md5 5f2b0c6e, expected 11111111 (MyMod.dll)",
            "ERROR: code=expectation_mismatch mismatches=1"
        };
        ExpectationCheck check = PlanExpectations.Judge(Pins, reply);
        Assert.Equal(ExpectationOutcome.Mismatch, check.Outcome);
        Assert.Equal(reply, check.Output);
        Assert.StartsWith("MISMATCH (pins.txt, strict", check.Summary());
    }

    [Fact]
    public void NoConfirmationIsNotAPass()
    {
        // An empty reply carries no error code, so a check that only looks for errors passes it.
        Assert.False(PlanExpectations.Judge(Pins, new List<string>()).Held);
        Assert.False(PlanExpectations.Judge(Pins, new List<string> { "Unknown command: 'cli_expect' is not a recognized command" }).Held);
        Assert.False(PlanExpectations.Judge(Pins, new List<string> { "OK: EXPECT holds", "ERROR: code=client_timeout" }).Held);
    }

    [Fact]
    public void AMismatchExitsSixBeforeFailedSteps()
    {
        ExpectationCheck held = PlanExpectations.Judge(Pins, new List<string> { "OK: EXPECT 1 expectation(s) met" });
        ExpectationCheck mismatch = PlanExpectations.Judge(Pins, new List<string> { "MISMATCH x", "ERROR: code=expectation_mismatch mismatches=1" });
        ExpectationCheck unreadable = PlanExpectations.Unreadable(Pins, "pins.txt has no expectations");

        Assert.Equal(CliExitCode.Success, PlanExpectations.ExitCode(new ExpectationCheck?[] { null }, 0));
        Assert.Equal(CliExitCode.Success, PlanExpectations.ExitCode(new[] { held }, 0));
        Assert.Equal(CliExitCode.CommandFailure, PlanExpectations.ExitCode(new[] { held }, 2));
        Assert.Equal(CliExitCode.ExpectationMismatch, PlanExpectations.ExitCode(new[] { mismatch }, 0));
        Assert.Equal(CliExitCode.ExpectationMismatch, PlanExpectations.ExitCode(new ExpectationCheck?[] { null, mismatch, unreadable }, 3));
        Assert.Equal(CliExitCode.BadInput, PlanExpectations.ExitCode(new[] { held, unreadable }, 1));
    }
}
