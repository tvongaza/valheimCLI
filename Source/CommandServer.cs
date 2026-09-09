using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using BepInEx.Logging;

namespace valheimCLI
{
    public class CommandServer : IDisposable
    {
        private readonly ManualLogSource _logger;
        private readonly int _port;
        private TcpListener? _listener;
        private Thread? _listenerThread;
        private volatile bool _running;
        private readonly RequestBroker _broker = new();
        /// <summary>Legacy CMD: requests wait this long; CMDT:&lt;seconds&gt;: sets its own.</summary>
        public const double DefaultTimeoutSeconds = 30;
        private readonly List<TcpClient> _clients = new();
        private readonly object _clientsLock = new();

        // State tracking
        private GameStateTracker? _stateTracker;
        private readonly List<StreamWriter> _stateSubscribers = new();
        private readonly object _subscribersLock = new();

        public CommandServer(ManualLogSource logger, int port = 5555)
        {
            _logger = logger;
            _port = port;
        }

        public void SetStateTracker(GameStateTracker tracker)
        {
            _stateTracker = tracker;
            _stateTracker.OnStateChanged += OnGameStateChanged;
        }

        private void OnGameStateChanged(GameState previousState, GameState newState)
        {
            string stateMessage = $"STATE_CHANGED:{GameStateTracker.StateToString(newState)}";
            BroadcastToSubscribers(stateMessage);
        }

        private void BroadcastToSubscribers(string message)
        {
            lock (_subscribersLock)
            {
                List<StreamWriter> deadSubscribers = new();

                foreach (StreamWriter writer in _stateSubscribers)
                {
                    try
                    {
                        writer.WriteLine(message);
                    }
                    catch
                    {
                        deadSubscribers.Add(writer);
                    }
                }

                foreach (StreamWriter dead in deadSubscribers)
                {
                    _stateSubscribers.Remove(dead);
                }
            }
        }

        public void Start()
        {
            if (_running) return;

            _running = true;
            _listenerThread = new Thread(ListenerLoop)
            {
                IsBackground = true,
                Name = "ValheimCLI-Server"
            };
            _listenerThread.Start();
            _logger.LogInfo($"Command server starting on port {_port}");
        }

        public void Stop()
        {
            _running = false;
            _listener?.Stop();

            lock (_clientsLock)
            {
                foreach (var client in _clients)
                {
                    try { client.Close(); } catch { }
                }
                _clients.Clear();
            }

            _logger.LogInfo("Command server stopped");
        }

        private void ListenerLoop()
        {
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, _port);
                _listener.Start();
                _logger.LogInfo($"Command server listening on 127.0.0.1:{_port}");

                while (_running)
                {
                    try
                    {
                        if (_listener.Pending())
                        {
                            var client = _listener.AcceptTcpClient();
                            _logger.LogInfo("CLI client connected");

                            lock (_clientsLock)
                            {
                                _clients.Add(client);
                            }

                            var clientThread = new Thread(() => HandleClient(client))
                            {
                                IsBackground = true,
                                Name = "ValheimCLI-Client"
                            };
                            clientThread.Start();
                        }
                        else
                        {
                            Thread.Sleep(100);
                        }
                    }
                    catch (SocketException) when (!_running)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Server error: {ex.Message}");
            }
        }

        private void HandleClient(TcpClient client)
        {
            StreamWriter? subscribedWriter = null;

            try
            {
                using NetworkStream stream = client.GetStream();
                using StreamReader reader = new StreamReader(stream, new UTF8Encoding(false));
                using StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

                writer.WriteLine("VALHEIM_CLI_READY");
                // Capabilities for clients that know to read them; older clients read one
                // greeting line and ignore lines that are not OUTPUT: or state changes.
                writer.WriteLine("VALHEIM_CLI_CAPS completion");

                while (_running && client.Connected)
                {
                    try
                    {
                        if (!stream.DataAvailable)
                        {
                            Thread.Sleep(50);
                            continue;
                        }

                        var line = reader.ReadLine();
                        if (line == null) break;

                        line = line.Trim();
                        if (string.IsNullOrEmpty(line)) continue;

                        if (line == "PING")
                        {
                            writer.WriteLine("PONG");
                            continue;
                        }

                        if (line == "STATE")
                        {
                            string currentState = _stateTracker != null
                                ? GameStateTracker.StateToString(_stateTracker.CurrentState)
                                : "Unknown";
                            writer.WriteLine($"STATE:{currentState}");
                            continue;
                        }

                        if (line == "STATUS")
                        {
                            string currentStatus = _stateTracker != null
                                ? _stateTracker.CurrentStatusLine
                                : "state=Unknown phase=unknown";
                            writer.WriteLine($"STATUS:{currentStatus}");
                            continue;
                        }

                        if (line == "SUBSCRIBE_STATE")
                        {
                            lock (_subscribersLock)
                            {
                                if (!_stateSubscribers.Contains(writer))
                                {
                                    _stateSubscribers.Add(writer);
                                    subscribedWriter = writer;
                                    _logger.LogInfo("Client subscribed to state changes");
                                }
                            }
                            writer.WriteLine("SUBSCRIBED");
                            continue;
                        }

                        if (line == "UNSUBSCRIBE_STATE")
                        {
                            lock (_subscribersLock)
                            {
                                _stateSubscribers.Remove(writer);
                                subscribedWriter = null;
                                _logger.LogInfo("Client unsubscribed from state changes");
                            }
                            writer.WriteLine("UNSUBSCRIBED");
                            continue;
                        }

                        if (line == "LIST_COMMANDS")
                        {
                            var commands = GetAvailableCommands();
                            writer.WriteLine($"COMMANDS:{commands.Count}");
                            foreach (var cmd in commands)
                            {
                                writer.WriteLine(cmd);
                            }
                            writer.WriteLine("END_COMMANDS");
                            continue;
                        }

                        if (line.StartsWith("CMD:") || line.StartsWith("CMDT:"))
                        {
                            // CMD:<text> (30 s) or CMDT:<seconds>:<text>. The response carries the
                            // whole output of THIS command: the socket thread waits for the game
                            // thread to complete it (or the async handler to, later).
                            double timeoutSeconds = DefaultTimeoutSeconds;
                            string command;
                            if (line.StartsWith("CMDT:"))
                            {
                                string rest = line.Substring(5);
                                int colon = rest.IndexOf(':');
                                if (colon <= 0 || !double.TryParse(rest.Substring(0, colon), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out timeoutSeconds))
                                {
                                    writer.WriteLine("OUTPUT:1");
                                    writer.WriteLine("ERROR: code=bad_request message=expected CMDT:<seconds>:<command>");
                                    writer.WriteLine("END_OUTPUT");
                                    continue;
                                }
                                command = rest.Substring(colon + 1).Trim();
                            }
                            else
                            {
                                command = line.Substring(4).Trim();
                            }
                            // Remove any invisible/control characters
                            command = new string(command.Where(c => !char.IsControl(c) && c >= 32).ToArray());
                            RequestBroker.Request request = _broker.Submit(command, timeoutSeconds);
                            _logger.LogInfo($"Queued CLI command #{request.Id} (timeout {timeoutSeconds:F0}s): {command}");

                            RequestBroker.Response response = _broker.Wait(request, Thread.Sleep);
                            if (!response.Completed)
                                _logger.LogWarning($"CLI command #{request.Id} timed out after {timeoutSeconds:F0}s: {command}");

                            writer.WriteLine($"OUTPUT:{response.Lines.Count}");
                            foreach (string outputLine in response.Lines)
                            {
                                writer.WriteLine(outputLine);
                            }
                            writer.WriteLine("END_OUTPUT");
                        }
                    }
                    catch (IOException)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Client handler error: {ex}");
            }
            finally
            {
                // Remove from state subscribers if present
                if (subscribedWriter != null)
                {
                    lock (_subscribersLock)
                    {
                        _stateSubscribers.Remove(subscribedWriter);
                    }
                }

                lock (_clientsLock)
                {
                    _clients.Remove(client);
                }
                try { client.Close(); } catch { }
                _logger.LogInfo("CLI client disconnected");
            }
        }

        public RequestBroker Broker => _broker;

        /// <summary>Game thread: the next request to execute.</summary>
        public bool TryGetPendingRequest(out RequestBroker.Request request)
        {
            return _broker.TryDequeue(out request);
        }

        /// <summary>Output for the request executing now.</summary>
        public void SendOutput(string output)
        {
            _broker.Output(output);
        }

        private List<string> GetAvailableCommands()
        {
            var result = new List<string>();
            try
            {
                if (Terminal.commands != null)
                {
                    foreach (var kvp in Terminal.commands)
                    {
                        var cmd = kvp.Value;
                        result.Add($"{kvp.Key}|{cmd.Description}|{(cmd.IsCheat ? "cheat" : "")}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error getting commands: {ex.Message}");
            }
            return result;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
