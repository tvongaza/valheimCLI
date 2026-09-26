using System.Net;
using System.Net.Sockets;
using valheimCLI;
using valheim_cli.Testing;
using Xunit;

namespace valheimCLI.Tests;

public class CommandResponseTests
{
    private const string Success = "OK: WORLD_CREATED name=probe seedName=seed uid=123";

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public Task LogNewlineDoesNotSwallowSuccessOrNextResponse(string newline) =>
        Exchange((reader, writer) =>
        {
            Assert.NotNull(reader.ReadLine());
            CommandResponse.Write(writer, new[] { "Unity log" + newline, Success });
            Assert.NotNull(reader.ReadLine());
            CommandResponse.Write(writer, new[] { "OK: second request" });
        }, client =>
        {
            List<string> first = client.SendCommand("cli_create_world probe seed");
            List<string> second = client.SendCommand("second");
            Assert.Equal(new[] { "Unity log", "", Success }, first);
            Assert.Equal(new[] { "OK: second request" }, second);
        });

    [Fact]
    public Task EmptyOutputAndMarkerLikePayloadRemainFramed() =>
        Exchange((reader, writer) =>
        {
            Assert.NotNull(reader.ReadLine());
            CommandResponse.Write(writer, Array.Empty<string>());
            Assert.NotNull(reader.ReadLine());
            CommandResponse.Write(writer, new[] { "END_OUTPUT", "OUTPUT:99", "STATE_CHANGED:payload", "", Success });
        }, client =>
        {
            Assert.Empty(client.SendCommand("empty"));
            Assert.Equal(new[] { "END_OUTPUT", "OUTPUT:99", "STATE_CHANGED:payload", "", Success }, client.SendCommand("payload"));
        });

    [Fact]
    public Task MixedMultilineOutputPreservesEveryPhysicalLine() =>
        Exchange((reader, writer) =>
        {
            Assert.NotNull(reader.ReadLine());
            writer.WriteLine("STATE_CHANGED:MainMenu");
            CommandResponse.Write(writer, new[] { "a\r\nb\rc\nd\n\n", Success });
        }, client =>
        {
            string? state = null;
            client.OnStateChanged += value => state = value;
            Assert.Equal(new[] { "a", "b", "c", "d", "", "", Success }, client.SendCommand("multiline"));
            Assert.Equal("MainMenu", state);
        });

    [Theory]
    [InlineData("OUTPUT:2\nUnity log\n\nOK: WORLD_CREATED name=probe\nEND_OUTPUT\n")]
    [InlineData("OUTPUT:-1\nEND_OUTPUT\n")]
    [InlineData("OUTPUT:nope\nEND_OUTPUT\n")]
    [InlineData("OUTPUT:2147483648\nEND_OUTPUT\n")]
    [InlineData("OUTPUT:2\nonly one\n")]
    [InlineData("OUTPUT:1\nOK: something\n")]
    [InlineData("")]
    public Task InvalidOrTruncatedFrameFailsAndDisconnects(string frame) =>
        Exchange((reader, writer) =>
        {
            Assert.NotNull(reader.ReadLine());
            writer.Write(frame);
            writer.Flush();
        }, client =>
        {
            CommandResult result = client.ExecuteCommand("probe");
            Assert.False(result.Ok);
            // Closed before any response is connection_closed (a stop or a live reload);
            // a broken frame after the response began is protocol_error.
            string code = frame.Length == 0 ? "ERROR: code=connection_closed" : "ERROR: code=protocol_error";
            Assert.Contains(result.Output, line => line.StartsWith(code));
            Assert.False(client.IsConnected);
            // Never reuse a stream whose next line may belong to the previous response.
            Assert.Throws<InvalidOperationException>(() => client.SendCommand("must not run"));
        });

    // No game or station: a loopback peer uses the production serializer and the
    // real CLI client parses its replies. Socket waits are bounded on both ends.
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
