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

            // Wait for ready message
            string? ready = _reader.ReadLine();
            if (ready != "VALHEIM_CLI_READY")
            {
                Disconnect();
                return false;
            }

            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public void Disconnect()
    {
        _subscribed = false;
        _reader?.Dispose();
        _writer?.Dispose();
        _stream?.Dispose();
        _client?.Close();
        _client = null;
    }

    public string GetState()
    {
        EnsureConnected();

        _writer!.WriteLine("STATE");
        string? response = _reader!.ReadLine();

        if (response != null && response.StartsWith("STATE:"))
        {
            return response.Substring(6);
        }

        return "Unknown";
    }

    public Dictionary<string, string> GetStatusDetails()
    {
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

        return ParseKeyValueStatus(response.Substring("STATUS:".Length));
    }

    private static Dictionary<string, string> ParseKeyValueStatus(string payload)
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

    public List<string> SendCommand(string command)
    {
        EnsureConnected();

        _writer!.WriteLine($"CMDT:{CommandTimeout.TotalSeconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)}:{command}");

        List<string> result = new();

        // May receive state change notifications before the response
        while (true)
        {
            string? line = _reader!.ReadLine();
            if (line == null) break;

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
                        string? outputLine = _reader.ReadLine();
                        if (outputLine != null)
                            result.Add(outputLine);
                    }
                }
                // Read END_OUTPUT marker
                _reader.ReadLine();
                break;
            }
        }

        return result;
    }

    public CommandResult ExecuteCommand(string command)
    {
        return CommandResult.FromOutput(command, SendCommand(command));
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

public record CommandInfo
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public bool IsCheat { get; init; }
}
