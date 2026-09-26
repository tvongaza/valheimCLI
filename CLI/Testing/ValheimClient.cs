using System.Net.Sockets;

namespace valheim_cli.Testing;

public class ValheimClient : IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private TcpClient? _client;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private NetworkStream? _stream;
    private bool _disposed;
    private bool _subscribed;

    public event Action<string>? OnStateChanged;
    public bool IsConnected => _client?.Connected ?? false;

    public ValheimClient(string host = ConnectionDefaults.Host, int port = ConnectionDefaults.Port)
    {
        _host = host;
        _port = port;
    }

    private void EnsureConnected()
    {
        if (_writer == null || _reader == null)
            throw new InvalidOperationException("Not connected");
    }

    private bool TryHandleStateChange(string? line)
    {
        if (line != null && line.StartsWith("STATE_CHANGED:"))
        {
            OnStateChanged?.Invoke(line.Substring(14));
            return true;
        }
        return false;
    }

    public bool Connect()
    {
        try
        {
            _client = new TcpClient();
            _client.Connect(_host, _port);
            _stream = _client.GetStream();
            _reader = new StreamReader(_stream, ConnectionDefaults.Utf8NoBom);
            _writer = new StreamWriter(_stream, ConnectionDefaults.Utf8NoBom) { AutoFlush = true };

            // Wait for ready message. The plugin's socket thread greets at once whatever
            // the game is doing; a tunnel whose far end is gone may accept and then say
            // nothing, so the greeting has a deadline instead of blocking a wait forever.
            _stream.ReadTimeout = (int)GreetingTimeout.TotalMilliseconds;
            string? ready = _reader.ReadLine();
            _stream.ReadTimeout = Timeout.Infinite;
            if (ready != "VALHEIM_CLI_READY")
            {
                Disconnect();
                return false;
            }

            // A server with command completion announces it on the next line; an older
            // server sends nothing more, so a short read timeout tells them apart.
            SupportsCompletion = false;
            int previous = _stream.ReadTimeout;
            try
            {
                _stream.ReadTimeout = 1500;
                string? caps = _reader.ReadLine();
                if (caps != null && caps.StartsWith("VALHEIM_CLI_CAPS"))
                    SupportsCompletion = caps.Contains("completion");
            }
            catch (IOException)
            {
                // no capability line: older server
            }
            finally
            {
                _stream.ReadTimeout = previous;
            }

            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (IOException)
        {
            // No greeting in time, or the connection closed during it.
            Disconnect();
            return false;
        }
    }

    /// <summary>How long Connect waits for the plugin's greeting.</summary>
    public static readonly TimeSpan GreetingTimeout = TimeSpan.FromSeconds(5);

    public void Disconnect()
    {
        _subscribed = false;
        _reader?.Dispose();
        _writer?.Dispose();
        _stream?.Dispose();
        _client?.Close();
        _client = null;
        // Dropped so a caller's finally does not touch a disposed stream and the
        // next call reports "Not connected" instead of writing to a dead socket.
        _stream = null;
        _reader = null;
        _writer = null;
    }

    /// <summary>
    /// The game state, or "Unknown" when the server does not answer. A server
    /// that has just closed or reset the connection (it unloaded: a live
    /// reload) is noticed here, not thrown: callers ask the state after a
    /// command, and the command's own result must stand.
    /// </summary>
    public string GetState()
    {
        if (_writer == null || _reader == null)
            return "Unknown";

        string? response;
        try
        {
            _writer.WriteLine("STATE");
            response = _reader.ReadLine();
        }
        catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ObjectDisposedException)
        {
            Disconnect();
            return "Unknown";
        }

        if (response == null)
        {
            Disconnect();
            return "Unknown";
        }

        if (response.StartsWith("STATE:"))
        {
            return response.Substring(6);
        }

        return "Unknown";
    }

    /// <summary>
    /// The last GetStatusDetails read the plugin's STATUS line. False when it fell back to the
    /// state alone (no STATUS line in time, or a plugin without one): then a field missing from
    /// the details says nothing about what the plugin reports.
    /// </summary>
    public bool StatusLineRead { get; private set; }

    public Dictionary<string, string> GetStatusDetails()
    {
        StatusLineRead = false;
        EnsureConnected();

        _writer!.WriteLine("STATUS");
        int previousReadTimeout = _stream?.ReadTimeout ?? Timeout.Infinite;
        string? response = null;
        try
        {
            if (_stream != null)
            {
                _stream.ReadTimeout = 1000;
            }

            response = _reader!.ReadLine();
        }
        catch (IOException)
        {
            response = null;
        }
        finally
        {
            if (_stream != null)
            {
                _stream.ReadTimeout = previousReadTimeout;
            }
        }

        if (response == null || !response.StartsWith("STATUS:"))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["state"] = GetState(),
                ["phase"] = "unknown"
            };
        }

        StatusLineRead = true;
        return ParseKeyValueStatus(response.Substring("STATUS:".Length));
    }

    internal static Dictionary<string, string> ParseKeyValueStatus(string payload)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        string[] parts = payload.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (string part in parts)
        {
            int equals = part.IndexOf('=');
            if (equals <= 0 || equals == part.Length - 1)
            {
                continue;
            }

            result[part[..equals]] = part[(equals + 1)..];
        }

        return result;
    }

    public bool SubscribeToStateChanges()
    {
        EnsureConnected();

        _writer!.WriteLine("SUBSCRIBE_STATE");
        string? response = _reader!.ReadLine();
        _subscribed = response == "SUBSCRIBED";
        return _subscribed;
    }

    public bool UnsubscribeFromStateChanges()
    {
        EnsureConnected();

        _writer!.WriteLine("UNSUBSCRIBE_STATE");
        string? response = _reader!.ReadLine();
        _subscribed = false;
        return response == "UNSUBSCRIBED";
    }

    /// <summary>Server-side wait for a command's completion; the response holds its whole output.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>Whether the connected server pairs responses with commands (CMDT); false for older servers.</summary>
    public bool SupportsCompletion { get; private set; }

    /// <summary>Extra time the client allows for the server's own timeout response before giving up on the socket.</summary>
    public static readonly TimeSpan ResponseAllowance = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Resend a command up to this many times when the game answers that it expired in the
    /// queue and never ran (--retry-unstarted); 0 sends once. A command that started is never resent.
    /// </summary>
    public int RetryUnstarted { get; set; }

    /// <summary>Gets one heartbeat line per resend.</summary>
    public Action<string>? OnRetry { get; set; }

    public List<string> SendCommand(string command)
    {
        return CommandRetry.Send(() => SendOnce(command), RetryUnstarted, OnRetry);
    }

    private List<string> SendOnce(string command)
    {
        EnsureConnected();

        List<string> result = new();
        if (SupportsCompletion)
        {
            _writer!.WriteLine($"CMDT:{CommandTimeout.TotalSeconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)}:{command}");
        }
        else
        {
            // Older server: it answers after the first output line (or 30 s) and a slow
            // command's output arrives with the next call. Say so once per connection.
            if (!_warnedNoCompletion)
            {
                _warnedNoCompletion = true;
                Console.Error.WriteLine("warning: this valheimCLI server has no command completion; --timeout bounds only this client's wait and a slow command's output may arrive with the next call");
            }
            _writer!.WriteLine($"CMD:{command}");
        }

        // The socket wait is bounded: the server's timeout plus an allowance for its
        // timeout response. On expiry the command is NOT resent: it may have executed.
        int previousReadTimeout = _stream!.ReadTimeout;
        _stream.ReadTimeout = (int)Math.Min(int.MaxValue, (CommandTimeout + ResponseAllowance).TotalMilliseconds);
        try
        {
        // May receive state change notifications before the response
        while (true)
        {
            string? line;
            try
            {
                line = _reader!.ReadLine();
            }
            catch (IOException ex) when (!ConnectionLoss.IsReadTimeout(ex))
            {
                line = null;
            }
            catch (IOException)
            {
                result.Add($"ERROR: code=client_timeout message=no response within {(CommandTimeout + ResponseAllowance).TotalSeconds:F0}s; the command was not resent (it may have executed); check the server is a valheimCLI with command completion");
                Disconnect();
                return result;
            }
            if (line == null)
            {
                // The server closed the connection before answering: it stopped,
                // or a live reload replaced it. Never report that as success.
                result.Add(ConnectionLoss.Line(command));
                Disconnect();
                return result;
            }

            // Handle state change notifications
            if (TryHandleStateChange(line))
                continue;

            // Handle command output
            if (line.StartsWith("OUTPUT:"))
            {
                if (int.TryParse(line.Substring(7), out int count))
                {
                    for (int i = 0; i < count; i++)
                    {
                        string? outputLine = _reader!.ReadLine();
                        if (outputLine != null)
                            result.Add(outputLine);
                    }
                }
                // Read END_OUTPUT marker
                _reader!.ReadLine();
                break;
            }
        }
        }
        finally
        {
            RestoreReadTimeout(previousReadTimeout);
        }

        return result;
    }

    /// <summary>
    /// Restores the read timeout after a command. The server may have reset the
    /// connection meanwhile (it unloaded right after answering); the socket
    /// option then fails (EINVAL on macOS), and the connection is dropped
    /// instead of failing the command that already has its answer.
    /// </summary>
    private void RestoreReadTimeout(int timeout)
    {
        if (_stream == null)
            return;
        try
        {
            _stream.ReadTimeout = timeout;
        }
        catch (Exception ex) when (ex is SocketException || ex is IOException || ex is ObjectDisposedException)
        {
            Disconnect();
        }
    }

    private bool _warnedNoCompletion;

    public CommandResult ExecuteCommand(string command)
    {
        List<string> output = SendCommand(command);
        return SilentReply.Judge(command, output, TryListCommandNames, $"{_host}:{_port}")
               ?? CommandResult.FromOutput(command, output);
    }

    /// <summary>How long TryListCommandNames waits for the plugin's command list.</summary>
    public static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The console command names the plugin has registered (LIST_COMMANDS, which every plugin build
    /// answers on its socket thread), or null when it did not list any in time. A list that did not
    /// arrive whole leaves the connection out of step, so it is closed.
    /// </summary>
    public IReadOnlyCollection<string>? TryListCommandNames()
    {
        if (_stream == null || _writer == null || _reader == null)
        {
            return null;
        }

        int previous = Timeout.Infinite;
        try
        {
            previous = _stream.ReadTimeout;
            _stream.ReadTimeout = (int)ListTimeout.TotalMilliseconds;
            _writer.WriteLine("LIST_COMMANDS");
            string? header = _reader.ReadLine();
            while (TryHandleStateChange(header))
            {
                header = _reader.ReadLine();
            }

            if (header == null || !header.StartsWith("COMMANDS:") || !int.TryParse(header.Substring(9), out int count))
            {
                Disconnect();
                return null;
            }

            HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < count; i++)
            {
                string? line = _reader.ReadLine();
                if (line == null)
                {
                    Disconnect();
                    return null;
                }

                int bar = line.IndexOf('|');
                names.Add(bar < 0 ? line : line[..bar]);
            }

            _reader.ReadLine(); // END_COMMANDS
            return names.Count > 0 ? names : null;
        }
        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
        {
            Disconnect();
            return null;
        }
        finally
        {
            try
            {
                if (_stream != null)
                    _stream.ReadTimeout = previous;
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public bool TryGetConnectionStatus(out string connectionStatus, out string server)
    {
        connectionStatus = "";
        server = "";
        CommandResult result = ExecuteCommand("cli_connection_status");
        foreach (string line in result.Output)
        {
            int statusIndex = line.IndexOf("connectionStatus=", StringComparison.OrdinalIgnoreCase);
            if (statusIndex < 0)
            {
                continue;
            }

            string payload = line[statusIndex..];
            string[] parts = payload.Split(',', StringSplitOptions.TrimEntries);
            foreach (string part in parts)
            {
                if (part.StartsWith("connectionStatus=", StringComparison.OrdinalIgnoreCase))
                {
                    connectionStatus = part["connectionStatus=".Length..];
                }
                else if (part.StartsWith("server=", StringComparison.OrdinalIgnoreCase))
                {
                    server = part["server=".Length..];
                }
            }
        }

        return !string.IsNullOrWhiteSpace(connectionStatus);
    }

    /// <summary>
    /// Wait for a specific game state with timeout
    /// </summary>
    public async Task<bool> WaitForStateAsync(
        string targetState,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        TimeSpan? interval = null)
    {
        if (_stream == null || _reader == null)
            throw new InvalidOperationException("Not connected");

        DateTime deadline = DateTime.Now.Add(timeout);
        DateTime lastPollTime = DateTime.MinValue;
        TimeSpan pollInterval = interval ?? TimeSpan.FromSeconds(2);

        while (DateTime.Now < deadline && !cancellationToken.IsCancellationRequested)
        {
            // Periodically poll the current state (more reliable than just push notifications)
            if (DateTime.Now - lastPollTime >= pollInterval)
            {
                string currentState = GetState();
                if (currentState.Equals(targetState, StringComparison.OrdinalIgnoreCase))
                    return true;
                lastPollTime = DateTime.Now;
            }

            // Also check for push notifications
            if (_stream.DataAvailable)
            {
                string? line = _reader.ReadLine();
                if (TryHandleStateChange(line))
                {
                    string newState = line!.Substring(14);
                    if (newState.Equals(targetState, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            else
            {
                await Task.Delay(100, cancellationToken);
            }
        }

        return false;
    }

    /// <summary>
    /// Poll for state changes (non-blocking)
    /// </summary>
    public void PollStateChanges()
    {
        if (_stream == null || _reader == null || !_subscribed)
            return;

        while (_stream.DataAvailable)
        {
            string? line = _reader.ReadLine();
            TryHandleStateChange(line);
        }
    }

    /// <summary>
    /// List available Valheim console commands
    /// </summary>
    public List<CommandInfo> ListCommands()
    {
        EnsureConnected();

        _writer!.WriteLine("LIST_COMMANDS");

        List<CommandInfo> result = new();
        string? header = _reader!.ReadLine();

        if (header != null && header.StartsWith("COMMANDS:"))
        {
            if (int.TryParse(header.Substring(9), out int count))
            {
                for (int i = 0; i < count; i++)
                {
                    string? line = _reader.ReadLine();
                    if (line != null)
                    {
                        string[] parts = line.Split('|');
                        if (parts.Length >= 2)
                        {
                            result.Add(new CommandInfo
                            {
                                Name = parts[0],
                                Description = parts[1],
                                IsCheat = parts.Length > 2 && parts[2] == "cheat"
                            });
                        }
                    }
                }
            }

            // Read END_COMMANDS marker
            _reader.ReadLine();
        }

        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
    }
}
