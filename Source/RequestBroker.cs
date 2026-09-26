using System;
using System.Collections.Generic;

namespace valheimCLI
{
    /// <summary>
    /// Pairs every CLI request with the output it produced. The socket thread
    /// submits a request and waits for its completion; the game thread dequeues
    /// it, runs it, sends output under its id and completes it. A command that
    /// keeps working after its handler returns (a coroutine: capture, arrive,
    /// env transition) marks itself async and completes later.
    ///
    /// Before this the server returned whatever output had accumulated 50 ms
    /// after the first line: a slow command's output came back with the NEXT
    /// call, so scripts asked twice and ran expensive commands twice. Now a
    /// response holds the whole output of its own command; a request that hits
    /// its timeout is abandoned and any output it produces later is dropped
    /// (counted, noted on the next response) instead of leaking forward.
    ///
    /// No sockets or Unity here so the CommandServer tests exercise it.
    /// </summary>
    public sealed class RequestBroker
    {
        public sealed class Request
        {
            public long Id;
            public string Text = "";
            public double TimeoutSeconds;
        }

        public sealed class Response
        {
            public long Id;
            public bool Completed;
            public List<string> Lines = new List<string>();
        }

        private readonly object _lock = new object();
        private long _nextId;
        private long _current;
        private readonly Queue<Request> _pending = new Queue<Request>();
        private readonly Dictionary<long, List<string>> _output = new Dictionary<long, List<string>>();
        private readonly HashSet<long> _completed = new HashSet<long>();
        private readonly HashSet<long> _async = new HashSet<long>();
        private readonly HashSet<long> _begunAsync = new HashSet<long>();
        private string? _shutdownLine;
        private readonly HashSet<long> _abandoned = new HashSet<long>();
        private readonly HashSet<long> _running = new HashSet<long>();
        private readonly HashSet<long> _expired = new HashSet<long>();
        private int _expiredSkipped;
        private readonly List<string> _orphans = new List<string>();
        private int _droppedSinceLastResponse;
        private long _lastDroppedFrom;

        /// <summary>Id of the request the game thread is executing right now (0 = none).</summary>
        public long CurrentRequestId
        {
            get { lock (_lock) return _current; }
            set { lock (_lock) _current = value; }
        }

        public int PendingCount { get { lock (_lock) return _pending.Count; } }

        /// <summary>The answer to every request still open when the plugin unloads.</summary>
        public const string UnloadedLine = "ERROR: code=unloaded message=valheimCLI was unloaded (a live reload or cli_self_unload) before this command completed; reconnect and retry";

        /// <summary>Socket thread: queue a request for the game thread.</summary>
        public Request Submit(string text, double timeoutSeconds)
        {
            lock (_lock)
            {
                Request request = new Request { Id = ++_nextId, Text = text, TimeoutSeconds = timeoutSeconds };
                _output[request.Id] = new List<string>();
                if (_shutdownLine != null)
                {
                    // The server is going away: answer at once, never run.
                    _output[request.Id].Add(_shutdownLine);
                    _completed.Add(request.Id);
                    return request;
                }
                _pending.Enqueue(request);
                return request;
            }
        }

        /// <summary>
        /// The server is stopping (its plugin is unloaded, e.g. by a live
        /// reload): every request not yet answered is completed with
        /// <paramref name="line"/>, queued ones never run, and later requests
        /// are answered with it at once. A waiting client gets an explicit error,
        /// never an empty success; the socket threads stop waiting.
        /// </summary>
        public void Shutdown(string line)
        {
            lock (_lock)
            {
                _shutdownLine = line;
                _pending.Clear();
                foreach (KeyValuePair<long, List<string>> entry in _output)
                {
                    if (_completed.Contains(entry.Key)) continue;
                    entry.Value.Add(line);
                    _completed.Add(entry.Key);
                }
                _running.Clear();
                _async.Clear();
            }
        }

        /// <summary>Requests whose response has not been taken yet (the socket thread still owes a reply).</summary>
        public int OpenCount { get { lock (_lock) return _output.Count; } }

        /// <summary>
        /// Game thread: next request to run. A request whose caller gave up while
        /// it was still queued (behind a long command) is expired here and never
        /// runs: its side effects would land after the caller moved on.
        /// </summary>
        public bool TryDequeue(out Request request)
        {
            lock (_lock)
            {
                while (_pending.Count > 0)
                {
                    Request next = _pending.Dequeue();
                    if (_abandoned.Contains(next.Id))
                    {
                        _expired.Add(next.Id);
                        _expiredSkipped++;
                        _output.Remove(next.Id);
                        continue;
                    }
                    _running.Add(next.Id);
                    request = next;
                    return true;
                }
                request = null!;
                return false;
            }
        }

        /// <summary>Requests that expired in the queue and were skipped (never executed).</summary>
        public int ExpiredSkipped { get { lock (_lock) return _expiredSkipped; } }

        public bool IsRunning(long id)
        {
            lock (_lock) return _running.Contains(id);
        }

        /// <summary>Output from whatever request is executing now (a handler's AddString).</summary>
        public void Output(string line) => Output(CurrentRequestId, line);

        /// <summary>Output for a specific request (an async command finishing later).</summary>
        public void Output(long id, string line)
        {
            lock (_lock)
            {
                if (id != 0 && _abandoned.Contains(id))
                {
                    _droppedSinceLastResponse++;
                    _lastDroppedFrom = id;
                    return;
                }
                if (id != 0 && _output.TryGetValue(id, out List<string> lines))
                {
                    lines.Add(line);
                    return;
                }
                // No request owns this line (a callback firing after its command
                // completed): it rides along with the next response, as before.
                _orphans.Add(line);
            }
        }

        /// <summary>The executing handler will complete this request itself, later.</summary>
        public void MarkAsync(long id)
        {
            lock (_lock)
            {
                _async.Add(id);
                _begunAsync.Add(id);
            }
        }

        /// <summary>Marked async and not completed yet.</summary>
        public bool IsAsync(long id)
        {
            lock (_lock) return _async.Contains(id);
        }

        /// <summary>
        /// The handler took this request async (BeginAsync), whether or not it
        /// has completed since. An async command can complete at once, inside
        /// its handler: the code after the handler must then neither add a
        /// confirmation line nor complete it again, and IsAsync is already false.
        /// </summary>
        public bool BegunAsync(long id)
        {
            lock (_lock) return _begunAsync.Contains(id);
        }

        /// <summary>
        /// The game thread's handler returned: complete the request unless the
        /// handler took it async (it completes itself, possibly already has).
        /// </summary>
        public void EndHandler(long id)
        {
            if (!BegunAsync(id))
                Complete(id);
        }

        /// <summary>Completing a request whose response was already taken (or that expired) does nothing.</summary>
        public void Complete(long id)
        {
            lock (_lock)
            {
                if (_output.ContainsKey(id))
                    _completed.Add(id);
                _async.Remove(id);
                _running.Remove(id);
            }
        }

        public bool IsComplete(long id)
        {
            lock (_lock) return _completed.Contains(id);
        }

        public bool IsAbandoned(long id)
        {
            lock (_lock) return _abandoned.Contains(id);
        }

        /// <summary>
        /// Socket thread: wait until the request completes or its timeout passes,
        /// then take its output. <paramref name="sleep"/> is called between polls
        /// with the poll interval in milliseconds (injected so tests need no clock).
        /// An incomplete request is abandoned: later output is dropped.
        /// </summary>
        public Response Wait(Request request, Action<int> sleep, Func<DateTime>? now = null, int pollMs = 20)
        {
            now ??= () => DateTime.UtcNow;
            DateTime deadline = now().AddSeconds(request.TimeoutSeconds);
            while (!IsComplete(request.Id) && now() < deadline)
                sleep(pollMs);
            return TakeResponse(request.Id);
        }

        /// <summary>Collect a request's output now; incomplete requests are abandoned.</summary>
        public Response TakeResponse(long id)
        {
            lock (_lock)
            {
                Response response = new Response { Id = id, Completed = _completed.Contains(id) };
                if (_output.TryGetValue(id, out List<string> lines))
                {
                    response.Lines.AddRange(lines);
                    _output.Remove(id);
                }
                if (response.Completed)
                {
                    _completed.Remove(id);
                    // Every async command says what happened. One that completed with
                    // nothing had its handler stopped (the plugin was unloaded):
                    // report that, never an empty success.
                    if (response.Lines.Count == 0 && _begunAsync.Contains(id))
                        response.Lines.Add(_shutdownLine ?? $"ERROR: code=no_output message=Command #{id} completed without output; its handler was stopped (the plugin was unloaded or reloaded)");
                }
                else
                {
                    _abandoned.Add(id);
                    string fate;
                    if (!_running.Contains(id))
                        fate = "it had not started and will not run.";
                    else if (_async.Contains(id))
                        fate = "it issues no further actions; an effect already started (a teleport, a screenshot write) settles first, and its later output is dropped.";
                    else
                        fate = "it still runs on the game thread (later requests queue behind it) and its output is dropped.";
                    response.Lines.Add($"ERROR: code=command_timeout message=Command #{id} did not complete in time; {fate}");
                    _async.Remove(id);
                }
                _begunAsync.Remove(id);
                if (_orphans.Count > 0)
                {
                    response.Lines.InsertRange(0, _orphans);
                    _orphans.Clear();
                }
                if (_droppedSinceLastResponse > 0)
                {
                    response.Lines.Add($"NOTE: dropped {_droppedSinceLastResponse} late output line(s) from request #{_lastDroppedFrom}");
                    _droppedSinceLastResponse = 0;
                }
                return response;
            }
        }
    }
}
