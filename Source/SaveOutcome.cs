namespace valheimCLI
{
    /// <summary>
    /// What cli_save saw, and the one line it answers with. The game saves the world
    /// on a thread and records success only in its save number: a save that writes
    /// every file moves it on (the files are _main.&lt;n&gt;.*), a failed one leaves
    /// it where it was and only logs. Pure .NET: the tests exercise it.
    /// </summary>
    public sealed class SaveOutcome
    {
        /// <summary>Why the game would not start a save ("" when it would).</summary>
        public string Skipped = "";

        /// <summary>The save thread was started by this request.</summary>
        public bool Started;

        /// <summary>The save thread has ended.</summary>
        public bool Finished;

        public uint SaveNumberBefore;
        public uint SaveNumberAfter;
        public long Milliseconds;
        public double TimeoutSeconds;
        public string World = "";
        public string Directory = "";

        /// <summary>
        /// The first reason, in the order ZNet.Save checks them, that the game would skip
        /// a world save without saying so to the caller (it only logs a warning).
        /// </summary>
        public static string SkipReason(bool sessionBlocksWorldSave, bool loadError, bool zoneSystemSkips, bool dungeonsSkip, bool enoughDiskSpace)
        {
            if (sessionBlocksWorldSave)
            {
                return "session_flag";
            }

            if (loadError)
            {
                return "load_error";
            }

            if (zoneSystemSkips)
            {
                return "zone_system";
            }

            if (dungeonsSkip)
            {
                return "dungeon_db";
            }

            if (!enoughDiskSpace)
            {
                return "low_disk";
            }

            return "";
        }

        public bool Saved => Skipped.Length == 0 && Started && Finished && SaveNumberAfter != SaveNumberBefore;

        public string Reply()
        {
            if (Skipped.Length > 0)
            {
                return $"ERROR: code=save_skipped reason={Skipped} message=the game would not start a world save; nothing was written (the game log says why)";
            }

            if (!Started)
            {
                return "ERROR: code=save_skipped reason=not_started message=the game did not start a save thread; nothing was written (the game log says why)";
            }

            if (!Finished)
            {
                return $"ERROR: code=save_timeout ms={Milliseconds} saveNumber={SaveNumberBefore} message=the save was still writing after {TimeoutSeconds:F0}s; it finishes on its own (a later cli_save waits for it)";
            }

            if (!Saved)
            {
                return $"ERROR: code=save_failed ms={Milliseconds} saveNumber={SaveNumberAfter} message=the save ended without moving the save number, so the game did not complete it (World save FAILED or Error saving world in the game log)";
            }

            return $"OK: SAVE ms={Milliseconds} world={World} saveNumber={SaveNumberAfter} dir={Directory}";
        }
    }
}
