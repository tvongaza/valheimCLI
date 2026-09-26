using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace valheimCLI.Extensions
{
    public enum ExtensionRole { Any, Server, Client }

    public sealed class ExtensionCommand
    {
        public string Name { get; }
        public string Help { get; }
        public bool ReadOnly { get; }
        public ExtensionRole Role { get; }
        public bool NeedsWorld { get; }
        public int ResultVersion { get; }
        public Func<ExtensionContext, IEnumerator> Execute { get; }
        public ExtensionCommand(string name, string help, Func<ExtensionContext, IEnumerator> execute,
            bool readOnly = false, ExtensionRole role = ExtensionRole.Any, bool needsWorld = false, int resultVersion = 1)
        {
            Name = name; Help = help; Execute = execute ?? throw new ArgumentNullException(nameof(execute));
            ReadOnly = readOnly; Role = role; NeedsWorld = needsWorld; ResultVersion = resultVersion;
        }
    }

    public sealed class ExtensionResult
    {
        public int ResultVersion { get; internal set; } = 1;
        public string ExtensionId { get; internal set; } = "";
        public string Instance { get; internal set; } = "";
        public bool Ok { get; }
        public string Code { get; }
        public string Message { get; }
        public IReadOnlyDictionary<string, object?> Data { get; }
        public ExtensionResult(bool ok, string code, string message, IDictionary<string, object?>? data = null)
        {
            Ok = ok; Code = code; Message = message;
            Data = new Dictionary<string, object?>(data ?? new Dictionary<string, object?>());
        }
    }

    public sealed class ExtensionContext
    {
        private readonly Func<bool> _cancelled;
        internal ExtensionResult? Result;
        internal Func<bool> Quiescent = () => true;
        public IReadOnlyList<string> Arguments { get; }
        public bool Cancelled => _cancelled();
        internal ExtensionContext(IEnumerable<string> args, Func<bool> cancelled)
        { Arguments = Array.AsReadOnly(args.ToArray()); _cancelled = cancelled; }
        public void Succeed(IDictionary<string, object?>? data = null) => Complete(new ExtensionResult(true, "", "Completed", data));
        public void Fail(string code, string message) => Complete(new ExtensionResult(false, code, message));
        private void Complete(ExtensionResult result)
        {
            if (Result != null) throw new InvalidOperationException("Extension already supplied a result.");
            Result = result;
        }
        /// <summary>For effects already issued: hold the shared gate until they actually settle after cancellation.</summary>
        public void WaitForQuiescence(Func<bool> probe) => Quiescent = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    public sealed class ExtensionRegistration : IDisposable
    {
        internal readonly ExtensionRegistry Registry;
        internal readonly List<Action> Cleanup = new List<Action>();
        internal readonly Dictionary<string, ExtensionCommand> Commands;
        internal int Active;
        internal bool Closing;
        public string Id { get; }
        public string Version { get; }
        public string Instance { get; } = Guid.NewGuid().ToString("N");
        public string CleanupError { get; internal set; } = "";
        internal ExtensionRegistration(ExtensionRegistry registry, string id, string version, Dictionary<string, ExtensionCommand> commands)
        { Registry = registry; Id = id; Version = version; Commands = commands; }
        public void OnDispose(Action cleanup)
        {
            Registry.CheckThread();
            if (Closing) throw new ObjectDisposedException(Id);
            Cleanup.Add(cleanup ?? throw new ArgumentNullException(nameof(cleanup)));
        }
        public void Dispose() => Registry.Retire(this);
    }

    /// <summary>Pure managed, main-thread-only lifecycle. No assembly loader, sockets or game dependency.</summary>
    public sealed class ExtensionRegistry : IDisposable
    {
        public const int ApiVersion = 1;
        private readonly int _thread = Thread.CurrentThread.ManagedThreadId;
        private readonly Dictionary<string, ExtensionRegistration> _owners = new Dictionary<string, ExtensionRegistration>(StringComparer.Ordinal);
        private readonly List<Work> _work = new List<Work>();
        private readonly OperationGate _gate;
        private readonly Func<ExtensionCommand, string?> _precondition;
        private bool _disposed;
        public ExtensionRegistry(OperationGate gate, Func<ExtensionCommand, string?> precondition)
        { _gate = gate; _precondition = precondition; }
        internal void CheckThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != _thread)
                throw new InvalidOperationException("Extension API must be called on the game's main thread.");
        }
        public ExtensionRegistration Register(string id, string version, int apiVersion, params ExtensionCommand[] commands)
        {
            CheckThread();
            if (_disposed) throw new ObjectDisposedException(nameof(ExtensionRegistry));
            if (apiVersion != ApiVersion) throw new NotSupportedException("Extension API version is unsupported.");
            if (!ValidName(id) || string.IsNullOrWhiteSpace(version)) throw new ArgumentException("Invalid extension identity.");
            if (_owners.ContainsKey(id)) throw new InvalidOperationException("Extension is already active, draining or has failed cleanup: " + id);
            var entries = new Dictionary<string, ExtensionCommand>(StringComparer.Ordinal);
            foreach (ExtensionCommand command in commands)
            {
                if (command == null || !ValidName(command.Name) || command.ResultVersion < 1 || entries.ContainsKey(command.Name))
                    throw new ArgumentException("Invalid or duplicate extension command.");
                entries.Add(command.Name, command);
            }
            var owner = new ExtensionRegistration(this, id, version, entries);
            _owners.Add(id, owner);
            return owner;
        }
        private static bool ValidName(string name) => !string.IsNullOrWhiteSpace(name) && name.All(c =>
            c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '.' || c == '_' || c == '-');
        public IReadOnlyList<ExtensionRegistration> Registrations
        { get { CheckThread(); return _owners.Values.ToArray(); } }
        public IReadOnlyList<ExtensionCommand> Commands(ExtensionRegistration owner)
        { CheckThread(); return owner.Closing ? Array.Empty<ExtensionCommand>() : owner.Commands.Values.ToArray(); }
        public bool IsClosing(ExtensionRegistration owner) => owner.Closing;

        public void Begin(string path, string[] arguments, long requestId, Func<bool> abandoned, Action<ExtensionResult> reply)
        {
            CheckThread();
            int slash = path.IndexOf('/');
            if (_disposed || slash <= 0 || !_owners.TryGetValue(path.Substring(0, slash), out ExtensionRegistration? owner) ||
                owner.Closing || !owner.Commands.TryGetValue(path.Substring(slash + 1), out ExtensionCommand? command))
            { reply(new ExtensionResult(false, "no_extension_command", "No active command: " + path)); return; }
            if (requestId == 0 || _work.Any(w => w.Id == requestId) || _work.Count >= 128)
            { reply(new ExtensionResult(false, "extension_queue", "Invalid request identity or extension queue full.")); return; }
            var context = new ExtensionContext(arguments, () => abandoned() || owner.Closing || _disposed);
            _work.Add(new Work { Owner = owner, Command = command, Context = context, Id = requestId, Reply = reply });
            owner.Active++;
        }
        public void Tick()
        {
            CheckThread();
            foreach (Work item in _work.ToArray())
            {
                try
                {
                    if (item.Context.Cancelled && !item.Stopping)
                    {
                        item.Stopping = true;
                        StopIterator(item);
                        item.Context.Result = new ExtensionResult(false, item.Owner.Closing ? "extension_unloaded" : "cancelled",
                            "Request stopped; effects already issued are not rolled back.");
                    }
                    if (!item.Stopping)
                    {
                        if (item.Iterator == null)
                        {
                            if (!item.Command.ReadOnly && !_gate.TryAcquire(item.Id, "extension:" + item.Owner.Id)) continue;
                            item.HoldsGate = !item.Command.ReadOnly;
                            string? refused = _precondition(item.Command);
                            if (refused != null)
                            {
                                item.Context.Fail("extension_precondition", refused);
                                item.Stopping = true;
                            }
                            else item.Iterator = item.Command.Execute(item.Context) ?? throw new InvalidOperationException("Handler returned no iterator.");
                        }
                        if (!item.Stopping && item.Iterator != null)
                        {
                            bool more = item.Iterator.MoveNext();
                            if (!more || item.Context.Result != null)
                            {
                                item.Stopping = true;
                                StopIterator(item);
                                if (item.Context.Result == null) item.Context.Fail("missing_result", "Handler ended without a result.");
                            }
                            else if (item.Iterator.Current != null)
                                throw new InvalidOperationException("Extension handlers yield null; inspect readiness on the next tick.");
                        }
                    }
                    if (item.Stopping && !item.DisposalFailed && item.Context.Quiescent()) Finish(item);
                }
                catch (Exception ex)
                {
                    item.Stopping = true;
                    try { StopIterator(item); }
                    catch (Exception disposeError) { item.Owner.CleanupError = disposeError.Message; }
                    item.Iterator = null;
                    item.Context.Result = new ExtensionResult(false, "extension_exception", ex.Message);
                    // A throwing quiescence probe cannot prove safety. Keep the gate and owner;
                    // diagnostics show draining and a process restart clears it.
                    try { if (!item.DisposalFailed && item.Context.Quiescent()) Finish(item); }
                    catch (Exception probeError) { item.Owner.CleanupError = probeError.Message; }
                }
            }
        }
        private static void StopIterator(Work item)
        {
            var iterator = item.Iterator as IDisposable;
            item.Iterator = null; // Clear before disposal: a throwing cleanup must never run twice.
            try { iterator?.Dispose(); }
            catch (Exception error)
            {
                item.DisposalFailed = true;
                item.Owner.CleanupError = "Handler cleanup failed; restart required: " + error.Message;
                throw;
            }
        }
        private void Finish(Work item)
        {
            if (!_work.Remove(item)) return;
            item.Context.Result!.ResultVersion = item.Command.ResultVersion;
            item.Context.Result.ExtensionId = item.Owner.Id;
            item.Context.Result.Instance = item.Owner.Instance;
            if (item.HoldsGate) _gate.Release(item.Id);
            item.Owner.Active--;
            try { item.Reply(item.Context.Result!); }
            finally { CleanupIfReady(item.Owner); }
        }
        internal void Retire(ExtensionRegistration owner)
        {
            CheckThread();
            if (owner.Closing) return;
            owner.Closing = true;
            owner.Commands.Clear();
            CleanupIfReady(owner);
        }
        private void CleanupIfReady(ExtensionRegistration owner)
        {
            if (!owner.Closing || owner.Active != 0) return;
            foreach (Action action in owner.Cleanup.AsEnumerable().Reverse())
            {
                try { action(); }
                catch (Exception ex) { owner.CleanupError += ex.Message + "; "; }
            }
            owner.Cleanup.Clear();
            if (owner.CleanupError.Length == 0 && _owners.TryGetValue(owner.Id, out ExtensionRegistration? current) && ReferenceEquals(current, owner))
                _owners.Remove(owner.Id);
        }
        public void Dispose()
        {
            CheckThread(); _disposed = true;
            foreach (ExtensionRegistration owner in _owners.Values.ToArray()) Retire(owner);
            Tick();
        }
        private sealed class Work
        {
            public ExtensionRegistration Owner = null!;
            public ExtensionCommand Command = null!;
            public ExtensionContext Context = null!;
            public long Id;
            public Action<ExtensionResult> Reply = null!;
            public IEnumerator? Iterator;
            public bool HoldsGate;
            public bool Stopping;
            public bool DisposalFailed;
        }
    }
}
