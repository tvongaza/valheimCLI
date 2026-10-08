using System;
using System.Diagnostics;
using System.Threading;

namespace valheimCLI
{
    /// <summary>
    /// Facts that the socket thread can read while the Unity thread is busy. The clock is monotonic;
    /// no Unity member is touched by a status reader. A missing first frame is reported as -1.
    /// </summary>
    public sealed class MainThreadHeartbeat
    {
        private readonly Func<long> _ticks;
        private readonly long _frequency;
        private long _lastFrame = long.MinValue;
        private readonly object _busyLock = new();
        private string? _busy;
        private long _busySince;

        public MainThreadHeartbeat() : this(Stopwatch.GetTimestamp, Stopwatch.Frequency) { }

        internal MainThreadHeartbeat(Func<long> ticks, long frequency)
        {
            _ticks = ticks ?? throw new ArgumentNullException(nameof(ticks));
            if (frequency <= 0) throw new ArgumentOutOfRangeException(nameof(frequency));
            _frequency = frequency;
        }

        /// <summary>Called once at the start of each plugin Update, before any game-state inspection.</summary>
        public void Stamp() => Interlocked.Exchange(ref _lastFrame, _ticks());

        /// <summary>
        /// State a mod can publish before deliberately holding the game thread. STATUS percent-encodes the
        /// bounded note so it remains one key/value token; a client decodes it after parsing the status line.
        /// </summary>
        public void SetBusy(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason) || reason.Length > 120 || Array.Exists(reason.ToCharArray(), char.IsControl))
                throw new ArgumentException("A busy note must contain 1 to 120 printable characters.", nameof(reason));
            lock (_busyLock) { _busy = reason; _busySince = _ticks(); }
        }

        public void ClearBusy() { lock (_busyLock) { _busy = null; _busySince = 0; } }

        /// <summary>Fields appended to the legacy STATUS payload, safe to form on a socket thread.</summary>
        public string StatusFields(RequestBroker broker)
        {
            if (broker == null) throw new ArgumentNullException(nameof(broker));
            long now = _ticks();
            long frame = Interlocked.Read(ref _lastFrame);
            long idleMs = frame == long.MinValue ? -1 : ElapsedMs(now, frame);
            RequestBroker.WorkSnapshot work = broker.SnapshotWork();
            string? busy;
            long busySince;
            lock (_busyLock) { busy = _busy; busySince = _busySince; }
            return "mainThreadIdleMs=" + idleMs + " queued=" + work.Queued +
                " running=" + (work.RunningId == 0 ? "none" : work.RunningId.ToString()) +
                " runningMs=" + work.RunningMs +
                " busy=" + (busy == null ? "none" : Uri.EscapeDataString(busy)) +
                " busyMs=" + (busy == null ? 0 : ElapsedMs(now, busySince));
        }

        private long ElapsedMs(long now, long since) =>
            (long)Math.Min(long.MaxValue, Math.Max(0d, (double)(now - since) * 1000d / _frequency));
    }
}
