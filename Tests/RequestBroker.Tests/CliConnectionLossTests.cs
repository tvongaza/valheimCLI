using System.Net;
using System.Net.Sockets;
using System.Text;
using valheim_cli.Testing;
using Xunit;

namespace valheimCLI.Tests;

/// <summary>
/// A connection that closes before its command answers (the server stopped,
/// or a live reload replaced valheimCLI) is an error with its own code, never
/// an empty success; the client drops the dead connection so the next call
/// reconnects. Played against a loopback server that speaks the protocol.
/// </summary>
public class CliConnectionLossTests
{
    /// <summary>Greets like the mod, reads one command, then runs <paramref name="afterCommand"/>.</summary>
    private static (Task server, int port) Serve(Action<TcpClient, StreamWriter> afterCommand)
    {
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task server = Task.Run(() =>
        {
            try
            {
                using TcpClient client = listener.AcceptTcpClient();
                NetworkStream stream = client.GetStream();
                StreamReader reader = new StreamReader(stream, new UTF8Encoding(false));
                StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                writer.WriteLine("VALHEIM_CLI_READY");
                writer.WriteLine("VALHEIM_CLI_CAPS completion");
                reader.ReadLine();
                afterCommand(client, writer);
            }
            finally
            {
                listener.Stop();
            }
        });
        return (server, port);
    }

    [Fact]
    public async Task ACloseBeforeTheAnswerIsAConnectionClosedError()
    {
        (Task server, int port) = Serve((client, _) => client.Close());
        using ValheimClient cli = new ValheimClient("127.0.0.1", port) { CommandTimeout = TimeSpan.FromSeconds(10) };
        Assert.True(cli.Connect());

        CommandResult result = cli.ExecuteCommand("cli_await_plugin valheimCLI.valheimCLI abcdef");

        Assert.False(result.Ok);
        Assert.Equal(ConnectionLoss.ErrorCode, result.ErrorCode);
        Assert.Equal((int)CliExitCode.ConnectionFailure, result.ExitCode);
        Assert.Single(result.Output);
        Assert.StartsWith("ERROR: code=connection_closed", result.Output[0]);
        Assert.Contains("cli_await_plugin", result.Output[0]);
        Assert.False(cli.IsConnected);
        await server.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task AResetBeforeTheAnswerIsAlsoAConnectionClosedError()
    {
        (Task server, int port) = Serve((client, _) =>
        {
            client.LingerState = new LingerOption(true, 0); // close with RST, not FIN
            client.Close();
        });
        using ValheimClient cli = new ValheimClient("127.0.0.1", port) { CommandTimeout = TimeSpan.FromSeconds(10) };
        Assert.True(cli.Connect());

        CommandResult result = cli.ExecuteCommand("cli_build");

        Assert.Equal(ConnectionLoss.ErrorCode, result.ErrorCode);
        Assert.False(cli.IsConnected);
        await server.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task AnAnswerStillReadsAsBefore()
    {
        (Task server, int port) = Serve((_, writer) =>
        {
            writer.WriteLine("OUTPUT:1");
            writer.WriteLine("OK: BUILD guid=valheimCLI.valheimCLI");
            writer.WriteLine("END_OUTPUT");
        });
        using ValheimClient cli = new ValheimClient("127.0.0.1", port) { CommandTimeout = TimeSpan.FromSeconds(10) };
        Assert.True(cli.Connect());

        CommandResult result = cli.ExecuteCommand("cli_build");

        Assert.True(result.Ok);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new[] { "OK: BUILD guid=valheimCLI.valheimCLI" }, result.Output);
        await server.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ASilentServerIsAClientTimeoutNotAnException()
    {
        // The client waits CommandTimeout plus its allowance (5 s) for an answer.
        TaskCompletionSource release = new TaskCompletionSource();
        (Task server, int port) = Serve((_, _) => release.Task.Wait(TimeSpan.FromSeconds(30)));
        using ValheimClient cli = new ValheimClient("127.0.0.1", port) { CommandTimeout = TimeSpan.Zero };
        Assert.True(cli.Connect());

        CommandResult result = cli.ExecuteCommand("cli_build");

        release.SetResult();
        Assert.StartsWith("ERROR: code=client_timeout", Assert.Single(result.Output));
        Assert.False(cli.IsConnected);
        await server.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void AServerErrorKeepsItsGeneralCode()
    {
        CommandResult result = CommandResult.FromOutput("x", new List<string> { "ERROR: code=await_timeout message=not reloaded" });
        Assert.Equal("command_failed", result.ErrorCode);
        Assert.Equal((int)CliExitCode.CommandFailure, result.ExitCode);
    }

    [Fact]
    public async Task AskingTheStateAfterAnUnloadedAnswerSaysUnknownInsteadOfThrowing()
    {
        // The server answers "unloaded" and closes; the client has not noticed
        // the close yet. The JSON output then asks the state, which threw and
        // turned the result into unexpected_exception.
        (Task server, int port) = Serve((client, writer) =>
        {
            writer.WriteLine("OUTPUT:1");
            writer.WriteLine("ERROR: code=unloaded message=valheimCLI was unloaded (a live reload or cli_self_unload) before this command completed; reconnect and retry");
            writer.WriteLine("END_OUTPUT");
            client.LingerState = new LingerOption(true, 0);
            client.Close();
        });
        using ValheimClient cli = new ValheimClient("127.0.0.1", port) { CommandTimeout = TimeSpan.FromSeconds(10) };
        Assert.True(cli.Connect());

        CommandResult result = cli.ExecuteCommand("cli_until 30 ready=true cli_build");
        await server.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ConnectionLoss.UnloadedCode, result.ErrorCode);
        Assert.Equal((int)CliExitCode.ConnectionFailure, result.ExitCode);
        Assert.Equal("Unknown", cli.GetState());
        Assert.False(cli.IsConnected);
        Assert.Equal("Unknown", cli.GetState());
    }

    [Fact]
    public void AServerThatUnloadedWithTheCommandOpenAlsoExitsThree()
    {
        CommandResult result = CommandResult.FromOutput("x", new List<string>
        {
            "ERROR: code=unloaded message=valheimCLI was unloaded (a live reload or cli_self_unload) before this command completed; reconnect and retry"
        });
        Assert.Equal(ConnectionLoss.UnloadedCode, result.ErrorCode);
        Assert.Equal((int)CliExitCode.ConnectionFailure, result.ExitCode);
    }

    [Fact]
    public void OnlyAReceiveTimeoutCountsAsATimeout()
    {
        Assert.True(ConnectionLoss.IsReadTimeout(new IOException("t", new SocketException((int)SocketError.TimedOut))));
        Assert.True(ConnectionLoss.IsReadTimeout(new IOException("t", new SocketException((int)SocketError.WouldBlock))));
        Assert.False(ConnectionLoss.IsReadTimeout(new IOException("r", new SocketException((int)SocketError.ConnectionReset))));
        Assert.False(ConnectionLoss.IsReadTimeout(new IOException("closed")));
    }
}
