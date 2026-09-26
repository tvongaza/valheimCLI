namespace valheimCLI
{
    internal static class WorldFixturePolicy
    {
        internal static bool CanCreate(bool atMenu, bool exists, bool overwrite, bool local, out string error)
        {
            if (!atMenu)
            {
                error = "create a world only at the main menu";
                return false;
            }
            if (exists && !local)
            {
                error = "a non-local world has this name; choose a different fixture name";
                return false;
            }
            if (exists && !overwrite)
            {
                error = "world already exists; choose another name or explicitly pass --overwrite";
                return false;
            }
            error = string.Empty;
            return true;
        }
    }
}
