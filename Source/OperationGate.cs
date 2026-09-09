namespace valheimCLI
{
    /// <summary>
    /// One owner at a time for operations that share the player and the
    /// camera (teleport, camera pose, screenshot, environment). A second
    /// async command waits its turn instead of moving the camera under a
    /// capture in progress. A command whose request timed out keeps the gate
    /// until the effect it already started (a teleport, a screenshot write)
    /// has settled, so the next command cannot overlap it.
    /// Pure .NET: the tests exercise it.
    /// </summary>
    public sealed class OperationGate
    {
        private readonly object _lock = new object();
        private long _owner;
        private string _ownerName = "";

        public long Owner { get { lock (_lock) return _owner; } }
        public string OwnerName { get { lock (_lock) return _ownerName; } }
        public bool IsFree { get { lock (_lock) return _owner == 0; } }

        /// <summary>Take the gate for <paramref name="id"/> if nobody holds it (or it already holds it).</summary>
        public bool TryAcquire(long id, string name)
        {
            lock (_lock)
            {
                if (_owner != 0 && _owner != id)
                    return false;
                _owner = id;
                _ownerName = name;
                return true;
            }
        }

        public bool IsOwner(long id)
        {
            lock (_lock) return _owner == id && id != 0;
        }

        /// <summary>Release the gate; only its owner can.</summary>
        public bool Release(long id)
        {
            lock (_lock)
            {
                if (_owner != id)
                    return false;
                _owner = 0;
                _ownerName = "";
                return true;
            }
        }
    }
}
