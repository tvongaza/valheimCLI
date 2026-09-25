using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using BepInEx.Logging;

namespace valheimCLI
{
    public enum GameState
    {
        Unknown,
        MainMenu,
        Loading,
        InWorld,
        InWorldNoPlayer
    }

    public class GameStateTracker
    {
        private static readonly FieldInfo? GameRespawnWaitField = typeof(Game).GetField("m_respawnWait", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo? ZNetInConnectingScreenMethod = typeof(ZNet).GetMethod("InConnectingScreen", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly PropertyInfo? ZoneSystemLocationsGeneratedProperty = typeof(ZoneSystem).GetProperty("LocationsGenerated", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo? ZoneSystemLocationsGeneratedField = typeof(ZoneSystem).GetField("m_locationsGenerated", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly PropertyInfo? ZoneSystemGenerateLocationsProgressProperty = typeof(ZoneSystem).GetProperty("GenerateLocationsProgress", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo? ZoneSystemGenerateLocationsProgressField = typeof(ZoneSystem).GetField("m_generateLocationsProgress", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo? ZoneSystemEstimatedLocationSecondsMethod = typeof(ZoneSystem).GetMethod("GetEstimatedGenerationCompletionTimeFromNow", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo? ZoneSystemGetLocationListMethod = typeof(ZoneSystem).GetMethod("GetLocationList", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo? ZoneSystemIsActiveAreaLoadedMethod = typeof(ZoneSystem).GetMethod("IsActiveAreaLoaded", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo? ZNetHostSocketField = typeof(ZNet).GetField("m_hostSocket", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private readonly ManualLogSource _logger;
        private GameState _currentState = GameState.Unknown;
        private GameState _previousState = GameState.Unknown;
        private string _currentStatusLine = "state=Unknown phase=unknown";

        public event Action<GameState, GameState>? OnStateChanged;

        public GameState CurrentState => _currentState;
        public GameState PreviousState => _previousState;
        public string CurrentStatusLine => _currentStatusLine;

        public GameStateTracker(ManualLogSource logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Call this from Update() to poll game state
        /// </summary>
        public void Update()
        {
            GameState newState = DetectCurrentState();
            _currentStatusLine = BuildStatusLine(newState);

            if (newState != _currentState)
            {
                _previousState = _currentState;
                _currentState = newState;
                _logger.LogInfo($"Game state changed: {_previousState} -> {_currentState}");
                OnStateChanged?.Invoke(_previousState, _currentState);
            }
        }

        private GameState DetectCurrentState()
        {
            // Check if we're in the main menu
            // FejdStartup is the main menu controller
            if (FejdStartup.instance != null && Game.instance == null)
            {
                return GameState.MainMenu;
            }

            // Check if Game exists (we're in a world)
            if (Game.instance != null)
            {
                // Check if ZNet is connecting
                if (IsZNetConnecting())
                {
                    return GameState.Loading;
                }

                // Check if player is loaded
                if (Player.m_localPlayer != null)
                {
                    return GameState.InWorld;
                }

                // Game exists but no player yet (loading/respawning)
                return GameState.InWorldNoPlayer;
            }

            // During initial load or transition
            if (IsZNetConnecting())
            {
                return GameState.Loading;
            }

            return GameState.Unknown;
        }

        private static string BuildStatusLine(GameState state)
        {
            string phase = DetectLoadPhase(state);
            bool gamePresent = Game.instance != null;
            // A logout keeps the player and the world for a few frames (or through a
            // long save); this says the world is on its way out, so a wait for the
            // main menu knows the menu is coming.
            bool shuttingDown = gamePresent && Game.instance!.IsShuttingDown();
            bool mainMenuPresent = FejdStartup.instance != null && Game.instance == null;
            bool localPlayerPresent = Player.m_localPlayer != null;
            ZNet? znet = ZNet.instance;
            ZoneSystem? zoneSystem = ZoneSystem.instance;
            bool znetPresent = znet != null;
            bool znetConnecting = IsZNetConnecting();
            bool zoneSystemPresent = zoneSystem != null;
            bool znetScenePresent = ZNetScene.instance != null;
            bool locationsGenerated = IsLocationsGenerated(zoneSystem);
            float locationProgress = GetLocationProgress(zoneSystem);
            float estimatedLocationSeconds = NormalizeEstimatedSeconds(GetEstimatedLocationSeconds(zoneSystem));
            int locationCount = GetLocationCount(zoneSystem);
            bool activeAreaLoaded = false;
            if (zoneSystem != null && znet != null)
            {
                try
                {
                    activeAreaLoaded = IsActiveAreaLoaded(zoneSystem);
                }
                catch
                {
                    activeAreaLoaded = false;
                }
            }

            // A dedicated server has a world but never a local player: its state stays
            // InWorldNoPlayer. It is ready for players once its locations exist and it
            // listens for them, which it does on every boot path (a new world opens after
            // generating its locations, an existing one right after loading).
            bool dedicated = IsDedicatedServer();
            bool listening = IsListening(znet);

            float respawnWait = 0f;
            if (Game.instance != null && GameRespawnWaitField != null)
            {
                object? value = GameRespawnWaitField.GetValue(Game.instance);
                if (value is float wait)
                {
                    respawnWait = wait;
                }
            }

            return "state=" + StateToString(state) +
                   " phase=" + phase +
                   " game=" + Bool(gamePresent) +
                   " shuttingDown=" + Bool(shuttingDown) +
                   " mainMenu=" + Bool(mainMenuPresent) +
                   " localPlayer=" + Bool(localPlayerPresent) +
                   " znet=" + Bool(znetPresent) +
                   " znetConnecting=" + Bool(znetConnecting) +
                   " znetScene=" + Bool(znetScenePresent) +
                   " zoneSystem=" + Bool(zoneSystemPresent) +
                   " locationsGenerated=" + Bool(locationsGenerated) +
                   " locationProgress=" + locationProgress.ToString("F3", CultureInfo.InvariantCulture) +
                   " estimatedLocationSeconds=" + estimatedLocationSeconds.ToString("F1", CultureInfo.InvariantCulture) +
                   " locationCount=" + locationCount.ToString(CultureInfo.InvariantCulture) +
                   " activeAreaLoaded=" + Bool(activeAreaLoaded) +
                   " respawnWait=" + respawnWait.ToString("F1", CultureInfo.InvariantCulture) +
                   " dedicated=" + Bool(dedicated) +
                   " listening=" + Bool(listening);
        }

        private static string DetectLoadPhase(GameState state)
        {
            if (state == GameState.MainMenu)
            {
                return "main_menu";
            }

            if (state == GameState.InWorld)
            {
                return "ready";
            }

            if (IsZNetConnecting())
            {
                return "connecting_screen";
            }

            if (Game.instance != null && IsDedicatedServer())
            {
                // No player to spawn and no active area of its own: a dedicated server
                // generates its locations (a new world), then opens for players.
                if (ZoneSystem.instance != null && !IsLocationsGenerated(ZoneSystem.instance))
                {
                    return "generating_locations";
                }

                return IsListening(ZNet.instance) ? "server_ready" : "opening_server";
            }

            if (Game.instance != null && Player.m_localPlayer == null)
            {
                if (ZoneSystem.instance != null && !IsLocationsGenerated(ZoneSystem.instance))
                {
                    return "generating_locations";
                }

                if (ZoneSystem.instance != null && ZNet.instance != null)
                {
                    try
                    {
                        if (!IsActiveAreaLoaded(ZoneSystem.instance))
                        {
                            return "loading_active_area";
                        }
                    }
                    catch
                    {
                        return "loading_active_area";
                    }
                }

                return "respawning";
            }

            if (state == GameState.Loading)
            {
                return "loading";
            }

            return "unknown";
        }

        private static bool s_dedicated;

        /// <summary>
        /// This process is a dedicated server. Known once ZNet exists; remembered, as a
        /// process never changes between the client and the server build.
        /// </summary>
        public static bool IsDedicatedServer()
        {
            if (!s_dedicated && ZNet.instance != null)
            {
                s_dedicated = ZNet.instance.IsDedicated();
            }

            return s_dedicated;
        }

        /// <summary>The game is a server (dedicated, or a host that opened its world) with its network socket open for players.</summary>
        private static bool IsListening(ZNet? znet)
        {
            if (znet == null || ZNetHostSocketField == null)
            {
                return false;
            }

            try
            {
                return znet.IsServer() && ZNetHostSocketField.GetValue(znet) != null;
            }
            catch
            {
                return false;
            }
        }

        private static float NormalizeEstimatedSeconds(float seconds)
        {
            if (float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds < 0f || seconds > 86400f)
            {
                return -1f;
            }

            return seconds;
        }

        public static bool IsZNetConnecting()
        {
            if (ZNet.instance == null || ZNetInConnectingScreenMethod == null)
            {
                return false;
            }

            try
            {
                return ZNetInConnectingScreenMethod.Invoke(ZNet.instance, null) is bool isConnecting && isConnecting;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsLocationsGenerated(ZoneSystem? zoneSystem)
        {
            object? value = GetMemberValue(zoneSystem, ZoneSystemLocationsGeneratedProperty, ZoneSystemLocationsGeneratedField);
            return value is bool generated && generated;
        }

        private static float GetLocationProgress(ZoneSystem? zoneSystem)
        {
            object? value = GetMemberValue(zoneSystem, ZoneSystemGenerateLocationsProgressProperty, ZoneSystemGenerateLocationsProgressField);
            return value is float progress ? progress : 0f;
        }

        private static float GetEstimatedLocationSeconds(ZoneSystem? zoneSystem)
        {
            if (zoneSystem == null || ZoneSystemEstimatedLocationSecondsMethod == null)
            {
                return -1f;
            }

            try
            {
                return ZoneSystemEstimatedLocationSecondsMethod.Invoke(zoneSystem, null) is float seconds ? seconds : -1f;
            }
            catch
            {
                return -1f;
            }
        }

        private static int GetLocationCount(ZoneSystem? zoneSystem)
        {
            if (zoneSystem == null || ZoneSystemGetLocationListMethod == null)
            {
                return 0;
            }

            try
            {
                return ZoneSystemGetLocationListMethod.Invoke(zoneSystem, null) is ICollection locations ? locations.Count : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static bool IsActiveAreaLoaded(ZoneSystem? zoneSystem)
        {
            if (zoneSystem == null || ZoneSystemIsActiveAreaLoadedMethod == null)
            {
                return false;
            }

            try
            {
                return ZoneSystemIsActiveAreaLoadedMethod.Invoke(zoneSystem, null) is bool loaded && loaded;
            }
            catch
            {
                return false;
            }
        }

        private static object? GetMemberValue(object? instance, PropertyInfo? property, FieldInfo? field)
        {
            if (instance == null)
            {
                return null;
            }

            try
            {
                if (property != null)
                {
                    return property.GetValue(instance);
                }

                return field?.GetValue(instance);
            }
            catch
            {
                return null;
            }
        }

        private static string Bool(bool value)
        {
            return value ? "true" : "false";
        }

        public static string StateToString(GameState state)
        {
            return state switch
            {
                GameState.Unknown => "Unknown",
                GameState.MainMenu => "MainMenu",
                GameState.Loading => "Loading",
                GameState.InWorld => "InWorld",
                GameState.InWorldNoPlayer => "InWorldNoPlayer",
                _ => "Unknown"
            };
        }

        public static GameState StringToState(string state)
        {
            return state.ToLowerInvariant() switch
            {
                "unknown" => GameState.Unknown,
                "mainmenu" => GameState.MainMenu,
                "loading" => GameState.Loading,
                "inworld" => GameState.InWorld,
                "inworldnoplayer" => GameState.InWorldNoPlayer,
                _ => GameState.Unknown
            };
        }
    }
}
