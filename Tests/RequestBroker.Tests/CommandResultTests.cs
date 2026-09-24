using valheim_cli.Testing;
using Xunit;

namespace valheimCLI.Tests;

/// <summary>
/// The CLI reads a reply's lines to decide whether the command failed. A reply
/// that ends with the command's own OK: line has said it succeeded: words such
/// as "usage:" or "timed out" in its data lines are data, not a failure.
/// </summary>
public class CommandResultTests
{
    [Fact]
    public void DataThatLooksLikeAFailureDoesNotFailAReplyEndingInOk()
    {
        CommandResult result = CommandResult.FromOutput("cli_call Help.Text", new List<string>
        {
            "VALUE \"usage: foo; the request timed out\"",
            "OK: CALL Help.Text kind=property type=string",
        });

        Assert.True(result.Ok);
        Assert.Equal("", result.ErrorCode);
    }

    [Fact]
    public void AnErrorLineStillFailsAReplyEndingInOk()
    {
        CommandResult result = CommandResult.FromOutput("cli_x", new List<string>
        {
            "ERROR: code=partial one zone failed",
            "OK: done with errors",
        });

        Assert.False(result.Ok);
        Assert.Equal("command_failed", result.ErrorCode);
    }

    [Theory]
    [InlineData("Usage: cli_fly [on|off|toggle]", "bad_input")]
    [InlineData("The operation timed out", "command_timeout")]
    [InlineData("'cli_call' is not valid in the current context.", "wrong_game_context")]
    [InlineData("ERROR: code=no_type", "command_failed")]
    public void RepliesWithoutAnOkLineAreStillClassifiedByTheirText(string line, string code)
    {
        CommandResult result = CommandResult.FromOutput("cmd", new List<string> { line });

        Assert.False(result.Ok);
        Assert.Equal(code, result.ErrorCode);
    }

    [Fact]
    public void AnOkLineThatIsNotLastDoesNotExcuseTheLinesAfterIt()
    {
        CommandResult result = CommandResult.FromOutput("cmd", new List<string>
        {
            "OK: step one",
            "Usage: cmd <x>",
        });

        Assert.Equal("bad_input", result.ErrorCode);
    }
}
