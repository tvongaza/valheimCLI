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
        private readonly HashSet<long> _abandoned = new HashSet<long>();
        private readonly HashSet<long> _waiting = new HashSet<long>();
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

        /// <summary>Socket thread: queue a request for the game thread.</summary>
        public Request Submit(string text, double timeoutSeconds)
        {
            lock (_lock)
            {
                Request request = new Request { Id = ++_nextId, Text = text, TimeoutSeconds = timeoutSeconds };
                _pending.Enqueue(request);
                _output[request.Id] = new List<string>();
                _waiting.Add(request.Id);
                return request;
            }
        }

        /// <summary>Game thread: next request to run.</summary>
        public bool TryDequeue(out Request request)
        {
            lock (_lock)
            {
                if (_pending.Count == 0)
                {
                    request = null!;
                    return false;
                }
                request = _pending.Dequeue();
                return true;
            }
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
            lock (_lock) _async.Add(id);
        }

        public bool IsAsync(long id)
        {
            lock (_lock) return _async.Contains(id);
        }

        public void Complete(long id)
        {
            lock (_lock)
            {
                _completed.Add(id);
                _async.Remove(id);
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
                _waiting.Remove(id);
                if (response.Completed)
                {
                    _completed.Remove(id);
                }
                else
                {
                    _abandoned.Add(id);
                    response.Lines.Add(_async.Contains(id)
                        ? $"ERROR: code=command_timeout message=Command #{id} did not complete in time; it is cancelled and later output is dropped."
                        : $"ERROR: code=command_timeout message=Command #{id} did not complete in time; it still runs on the game thread (later requests queue behind it) and its output is dropped.");
                    _async.Remove(id);
                }
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
