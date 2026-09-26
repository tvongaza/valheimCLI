using System.Net;
using System.Net.Sockets;
using valheim_cli.Testing;
using Xunit;

namespace valheimCLI.Tests;

/// <summary>
/// A plugin answers every command it ran, at least with "Executed: &lt;command&gt;". A reply with no line
/// at all (a dedicated server prints nothing for a command it does not have) was shown as an empty
/// success; it is an error, and for a cli_ command the plugin's own command list says whether the
/// build has the command.
/// </summary>
public class SilentReplyTests
{
    private const string Plugin = "127.0.0.1:5555";

    private static readonly string[] OlderBuild = { "cli_connection_status", "cli_world_dump", "devcommands", "save" };
    private static readonly string[] NewerBuild = { "cli_connection_status", "cli_world_dump", "cli_save", "devcommands", "save" };

    private static CommandResult Execute(string command, List<string> output, IReadOnlyCollection<string>? registered, Action? listed = null)
    {
        return SilentReply.Judge(command, output, () =>
        {
            listed?.Invoke();
            return registered;
        }, Plugin) ?? CommandResult.FromOutput(command, output);
    }

    [Fact]
    public void AnEmptyReplyToACliCommandThePluginDoesNotHaveIsUnknownNamingThePlugin()
    {
        CommandResult result = Execute("cli_save 240", new List<string>(), OlderBuild);

        Assert.False(result.Ok);
        Assert.Equal("unknown_command", result.ErrorCode);
        Assert.Equal(
            "ERROR: code=unknown_command message=the valheimCLI plugin at 127.0.0.1:5555 has no command 'cli_save' " +
            "(its build registers 2 cli_ commands): the plugin build is older than this client or does not have the command; update the plugin",
            Assert.Single(result.Output));
        Assert.Equal(result.Output[0], result.Message);
    }

    [Fact]
    public void TheBareConfirmationOfACliCommandThePluginDoesNotHaveIsUnknown()
    {
        // A game whose console printed nothing for the unknown name still gets the plugin's confirmation.
        CommandResult result = Execute("cli_save 240", new List<string> { "Executed: cli_save 240" }, OlderBuild);

        Assert.Equal("unknown_command", result.ErrorCode);
        Assert.Equal(2, result.Output.Count);
    }

    [Fact]
    public void AnEmptyReplyWithoutACommandListIsNoOutput()
    {
        CommandResult result = Execute("cli_save 240", new List<string>(), registered: null);

        Assert.False(result.Ok);
        Assert.Equal("no_output", result.ErrorCode);
        Assert.Equal(
            "ERROR: code=no_output message=the game printed nothing for 'cli_save': the plugin did not list its commands afterwards " +
            "(the connection may have closed); this plugin build may not have the command (valheimCLI plugin at 127.0.0.1:5555)",
            Assert.Single(result.Output));
    }

    [Fact]
    public void AnEmptyReplyToAnyOtherCommandIsNoOutputWithoutAskingForTheList()
    {
        bool asked = false;
        CommandResult result = Execute("save", new List<string> { "" }, NewerBuild, () => asked = true);

        Assert.False(asked);
        Assert.Equal("no_output", result.ErrorCode);
        Assert.Equal(
            "ERROR: code=no_output message=the game printed nothing for 'save': this plugin build may not have the command (valheimCLI plugin at 127.0.0.1:5555)",
            Assert.Single(result.Output));
    }

    [Fact]
    public void AnEmptyReplyToACommandThePluginHasIsStillNoOutput()
    {
        CommandResult result = Execute("cli_save 240", new List<string>(), NewerBuild);

        Assert.Equal("no_output", result.ErrorCode);
        Assert.Contains("the plugin lists the command, which ended without a reply line", result.Message);
    }

    [Fact]
    public void AConfirmationOfARegisteredCommandStandsAsSuccess()
    {
        // A registered command that ran and printed nothing: the plugin says so, and that is a success.
        CommandResult result = Execute("cli_save 240", new List<string> { "Executed: cli_save 240" }, NewerBuild);

        Assert.True(result.Ok);
        Assert.Equal("", result.ErrorCode);
        Assert.Equal(new[] { "Executed: cli_save 240" }, result.Output);
    }

    [Theory]
    [InlineData("save", "Executed: save")]
    [InlineData("devcommands", "Dev commands: True")]
    [InlineData("cli_save 240", "OK: SAVED world=probe seconds=1.2")]
    [InlineData("cli_run_trusted save", "Executed: save")]
    public void AReplyWithOutputIsJudgedAsBeforeWithoutAskingForTheList(string command, string line)
    {
        bool asked = false;
        CommandResult result = Execute(command, new List<string> { line }, OlderBuild, () => asked = true);

        Assert.False(asked);
        Assert.True(result.Ok);
        Assert.Equal(new[] { line }, result.Output);
    }

    [Fact]
    public void AnUnknownCommandOnAClientStillHasItsOwnCode()
    {
        CommandResult result = Execute("cli_nope", new List<string> { "'cli_nope' is not a recognized command! Type 'help' to see a list of valid commands." }, OlderBuild);

        Assert.Equal("unknown_command", result.ErrorCode);
    }

    // --- Over the socket, as the CLI sends a command ------------------------------------

    [Fact]
    public Task AnEmptyReplyIsCheckedAgainstThePluginsCommandList() =>
        Exchange((reader, writer) =>
        {
            Assert.Equal("CMDT:1:cli_save 240", reader.ReadLine());
            writer.WriteLine("OUTPUT:0");
            writer.WriteLine("END_OUTPUT");
            Assert.Equal("LIST_COMMANDS", reader.ReadLine());
            writer.WriteLine("STATE_CHANGED:InWorldNoPlayer");
            writer.WriteLine($"COMMANDS:{OlderBuild.Length}");
            foreach (string name in OlderBuild)
            {
                writer.WriteLine($"{name}|{name} help|");
            }
            writer.WriteLine("END_COMMANDS");
            Assert.Equal("CMDT:1:cli_connection_status", reader.ReadLine());
            writer.WriteLine("OUTPUT:1");
            writer.WriteLine("OK: connectionStatus=Connected, server=");
            writer.WriteLine("END_OUTPUT");
        }, client =>
        {
            CommandResult result = client.ExecuteCommand("cli_save 240");
            Assert.Equal("unknown_command", result.ErrorCode);
            Assert.Contains("has no command 'cli_save' (its build registers 2 cli_ commands)", result.Message);

            // The connection stays in step for the next command.
            Assert.True(client.ExecuteCommand("cli_connection_status").Ok);
        });

    [Fact]
    public Task AnEmptyReplyFromAPluginThatListsNothingIsNoOutput() =>
        Exchange((reader, writer) =>
        {
            Assert.Equal("CMDT:1:cli_save", reader.ReadLine());
            writer.WriteLine("OUTPUT:0");
            writer.WriteLine("END_OUTPUT");
            Assert.Equal("LIST_COMMANDS", reader.ReadLine());
            writer.WriteLine("COMMANDS:0");
            writer.WriteLine("END_COMMANDS");
        }, client =>
        {
            CommandResult result = client.ExecuteCommand("cli_save");
            Assert.Equal("no_output", result.ErrorCode);
        });

    private static async Task Exchange(Action<StreamReader, StreamWriter> serve, Action<ValheimClient> check)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task server = Task.Run(async () =>
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using TcpClient connection = await listener.AcceptTcpClientAsync(cancel.Token);
            using NetworkStream stream = connection.GetStream();
            stream.ReadTimeout = stream.WriteTimeout = 5000;
            using var reader = new StreamReader(stream, ConnectionDefaults.Utf8NoBom);
            using var writer = new StreamWriter(stream, ConnectionDefaults.Utf8NoBom) { AutoFlush = true };
            writer.WriteLine("VALHEIM_CLI_READY");
            writer.WriteLine("VALHEIM_CLI_CAPS completion");
            serve(reader, writer);
        });
        using var client = new ValheimClient("127.0.0.1", port) { CommandTimeout = TimeSpan.FromSeconds(1) };
        try
        {
            Assert.True(client.Connect());
            check(client);
        }
        finally
        {
            client.Disconnect();
            await server;
        }
    }
}
