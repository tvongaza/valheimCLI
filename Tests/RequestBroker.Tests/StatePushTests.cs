using System.Net;
using System.Net.Sockets;
using valheimCLI;
using valheim_cli.Testing;
using Xunit;

namespace valheimCLI.Tests;

public class StatePushTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Fact]
    public Task PushAheadOfTheStateReplyIsRaisedNotTakenForIt() =>
        Exchange((reader, writer) =>
        {
            Assert.Equal("STATE", reader.ReadLine());
            writer.WriteLine("STATE_CHANGED:Loading");
            writer.WriteLine("STATE:InWorld");
        }, client =>
        {
            var pushed = new List<string>();
            client.OnStateChanged += pushed.Add;
            Assert.Equal("InWorld", client.GetState());
            Assert.Equal(new[] { "Loading" }, pushed);
            return Task.CompletedTask;
        });

    [Fact]
    public Task PushAheadOfSubscribedIsRaisedNotTakenForIt() =>
        Exchange((reader, writer) =>
        {
            Assert.Equal("SUBSCRIBE_STATE", reader.ReadLine());
            writer.WriteLine("STATE_CHANGED:MainMenu");
            writer.WriteLine("SUBSCRIBED");
            Assert.Equal("UNSUBSCRIBE_STATE", reader.ReadLine());
            writer.WriteLine("STATE_CHANGED:Loading");
            writer.WriteLine("UNSUBSCRIBED");
        }, client =>
        {
            var pushed = new List<string>();
            client.OnStateChanged += pushed.Add;
            Assert.True(client.SubscribeToStateChanges());
            Assert.True(client.UnsubscribeFromStateChanges());
            Assert.Equal(new[] { "MainMenu", "Loading" }, pushed);
            return Task.CompletedTask;
        });

    [Fact]
    public Task ReadStateChangeAwaitsThePushWithoutSendingAnything() =>
        Exchange((reader, writer) =>
        {
            Assert.Equal("SUBSCRIBE_STATE", reader.ReadLine());
            writer.WriteLine("SUBSCRIBED");
            Thread.Sleep(300); // The push comes later than the read starts.
            writer.WriteLine("STATE_CHANGED:InWorld");
            // Nothing else arrives before the client closes: the wait sent no polls.
            Assert.Null(reader.ReadLine());
        }, async client =>
        {
            string? raised = null;
            client.OnStateChanged += value => raised = value;
            Assert.True(client.SubscribeToStateChanges());
            Assert.Equal("InWorld", await client.ReadStateChangeAsync().WaitAsync(Bound));
            Assert.Equal("InWorld", raised);
            Assert.True(client.IsConnected);
        });

    [Fact]
    public Task ClosedConnectionEndsTheReadWithNull() =>
        Exchange((reader, writer) =>
        {
            Assert.Equal("SUBSCRIBE_STATE", reader.ReadLine());
            writer.WriteLine("SUBSCRIBED");
        }, async client =>
        {
            Assert.True(client.SubscribeToStateChanges());
            Assert.Null(await client.ReadStateChangeAsync().WaitAsync(Bound));
            Assert.False(client.IsConnected);
        });

    [Fact]
    public Task CancellingTheReadDisconnects() =>
        Exchange((reader, writer) =>
        {
            Assert.Equal("SUBSCRIBE_STATE", reader.ReadLine());
            writer.WriteLine("SUBSCRIBED");
            Assert.Null(reader.ReadLine()); // Silent until the client gives up and closes.
        }, async client =>
        {
            Assert.True(client.SubscribeToStateChanges());
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ReadStateChangeAsync(cancel.Token).WaitAsync(Bound));
            Assert.False(client.IsConnected);
            Assert.Throws<InvalidOperationException>(() => client.SendCommand("must not run"));
        });

    [Fact]
    public Task AnythingButAPushIsAProtocolError() =>
        Exchange((reader, writer) =>
        {
            Assert.Equal("SUBSCRIBE_STATE", reader.ReadLine());
            writer.WriteLine("SUBSCRIBED");
            writer.WriteLine("OUTPUT:1");
        }, async client =>
        {
            Assert.True(client.SubscribeToStateChanges());
            await Assert.ThrowsAsync<InvalidDataException>(() => client.ReadStateChangeAsync().WaitAsync(Bound));
            Assert.False(client.IsConnected);
        });

    [Fact]
    public Task ReadingRequiresASubscription() =>
        Exchange((_, _) => { }, async client =>
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.ReadStateChangeAsync());
            Assert.True(client.IsConnected);
        });

    [Fact]
    public Task SilentStatusLivenessProbeDoesNotFallBackToAnUnboundedStateRead() =>
        Exchange((reader, _) =>
        {
            Assert.Equal("STATUS", reader.ReadLine());
            // Keep the connection open: STATE would be an unsafe second read if STATUS went silent.
            Assert.Null(reader.ReadLine());
        }, client =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Assert.Empty(client.GetStatusDetails(fallbackToState: false));
            Assert.False(client.StatusLineRead);
            Assert.InRange(clock.Elapsed, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(3));
            return Task.CompletedTask;
        });

    // No game or station: a loopback peer speaks the line protocol to the real client. Socket waits are bounded on both ends.
    private static async Task Exchange(Action<StreamReader, StreamWriter> serve, Func<ValheimClient, Task> check)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task server = Task.Run(async () =>
        {
            using var cancel = new CancellationTokenSource(Bound);
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
            await check(client);
        }
        finally
        {
            client.Disconnect();
            await server.WaitAsync(Bound);
        }
    }
}
