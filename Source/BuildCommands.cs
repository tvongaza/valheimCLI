using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace valheimCLI
{
    public static class BuildCommands
    {
        private static readonly FieldInfo? BuildPiecesField = typeof(Player).GetField("m_buildPieces", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? PlacementGhostField = typeof(Player).GetField("m_placementGhost", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? PlaceRayMaskField = typeof(Player).GetField("m_placeRayMask", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo? UpdatePlacementGhostMethod = typeof(Player).GetMethod("UpdatePlacementGhost", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo? PieceRayTestMethod = typeof(Player).GetMethod("PieceRayTest", BindingFlags.Instance | BindingFlags.NonPublic);

        internal static void Register()
        {
            _ = new Terminal.ConsoleCommand("cli_build_list", "List hammer build pieces: cli_build_list [filter] [limit] [nocost]", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                string filter = "";
                int limit = 40;
                bool noCost = false;
                for (int i = 1; i < args.Length; i++)
                {
                    if (IsNoCostToken(args[i]))
                    {
                        noCost = true;
                    }
                    else if (int.TryParse(args[i], out int parsedLimit))
                    {
                        limit = parsedLimit;
                    }
                    else if (string.IsNullOrWhiteSpace(filter))
                    {
                        filter = args[i];
                    }
                }

                ListPieces(filter, Math.Max(1, limit), noCost, args.Context.AddString);
            });

            _ = new Terminal.ConsoleCommand("cli_build_nocost", "Set player no-placement-cost mode: cli_build_nocost <true|false>", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                if (args.Length < 2 || !bool.TryParse(args[1], out bool enabled))
                {
                    args.Context.AddString("Usage: cli_build_nocost <true|false>");
                    return;
                }

                SetNoCost(enabled, args.Context.AddString);
            });

            _ = new Terminal.ConsoleCommand("cli_build_rotate", "Set the placement ghost's rotation step, as the scroll wheel does: cli_build_rotate <step> (yaw = step * 22.5 degrees)", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                if (args.Length != 2 || !int.TryParse(args[1], out int step))
                {
                    args.Context.AddString("Usage: cli_build_rotate <step>");
                    return;
                }

                SetRotation(step, args.Context.AddString);
            }, isCheat: true);

            _ = new Terminal.ConsoleCommand("cli_build_select", "Select a hammer build piece: cli_build_select <prefab-or-name> [nocost]", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                if (args.Length < 2)
                {
                    args.Context.AddString("Usage: cli_build_select <prefab-or-name> [nocost]");
                    return;
                }

                bool noCost = HasNoCostFlag(args, 2);
                SelectPiece(args[1], noCost, args.Context.AddString);
            });

            _ = new Terminal.ConsoleCommand("cli_build_status", "Report current build placement status", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                bool updateGhost = args.Length < 2 || !args[1].Equals("raw", StringComparison.OrdinalIgnoreCase);
                PrintStatus(updateGhost, args.Context.AddString);
            });

            _ = new Terminal.ConsoleCommand("cli_build_probe", "Select and probe placement without placing: cli_build_probe <prefab-or-name|selected> [nocost]", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                string pieceName = args.Length >= 2 ? args[1] : "selected";
                bool noCost = HasNoCostFlag(args, 2);
                ProbePiece(pieceName, noCost, args.Context.AddString);
            });

            _ = new Terminal.ConsoleCommand("cli_build_probe_at", "Aim at a point and probe placement: cli_build_probe_at <prefab-or-name|selected> <x> <y> <z> [nocost]", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                if (args.Length < 5 || !TryParseVector(args, 2, out Vector3 point))
                {
                    args.Context.AddString("Usage: cli_build_probe_at <prefab-or-name|selected> <x> <y> <z> [nocost]");
                    return;
                }

                bool noCost = HasNoCostFlag(args, 5);
                ProbePieceAt(args[1], point, noCost, args.Context.AddString);
            });

            _ = new Terminal.ConsoleCommand("cli_build_try_place", "Select and try to place a piece: cli_build_try_place <prefab-or-name|selected> [nocost]", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                string pieceName = args.Length >= 2 ? args[1] : "selected";
                bool noCost = HasNoCostFlag(args, 2);
                TryPlace(pieceName, noCost, args.Context.AddString);
            });

            _ = new Terminal.ConsoleCommand("cli_build_try_place_at", "Aim at a point and try to place a piece: cli_build_try_place_at <prefab-or-name|selected> <x> <y> <z> [nocost]", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                if (args.Length < 5 || !TryParseVector(args, 2, out Vector3 point))
                {
                    args.Context.AddString("Usage: cli_build_try_place_at <prefab-or-name|selected> <x> <y> <z> [nocost]");
                    return;
                }

                bool noCost = HasNoCostFlag(args, 5);
                TryPlaceAt(args[1], point, noCost, args.Context.AddString);
            });
        }

        private static void ListPieces(string filter, int limit, bool noCost, Action<string> addOutput)
        {
            if (!TryGetBuildContext(noCost, addOutput, out Player player, out PieceTable buildPieces))
            {
                return;
            }

            List<Piece> matches = buildPieces.m_pieces
                .Select(gameObject => gameObject != null ? gameObject.GetComponent<Piece>() : null)
                .Where(piece => piece != null && MatchesPiece(piece, filter))
                .Cast<Piece>()
                .OrderBy(piece => PrefabName(piece.gameObject), StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToList();

            StringBuilder sb = new();
            foreach (Piece piece in matches)
            {
                bool available = player.IsPieceAvailable(piece);
                sb.AppendLine($"PIECE prefab={PrefabName(piece.gameObject)} display={SafeName(piece.m_name)} category={piece.m_category} available={available}");
            }

            sb.Append($"OK: listed={matches.Count} filter='{filter}' totalTablePieces={buildPieces.m_pieces.Count} noCost={player.NoCostCheat()}");
            EmitLines(sb, addOutput);
        }

        private static void SetRotation(int step, Action<string> addOutput)
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                addOutput("ERROR: No local player found");
                return;
            }

            // The same integer the scroll wheel increments; vanilla turns it into
            // Quaternion.Euler(0, m_placeRotationDegrees * m_placeRotation, 0).
            player.m_placeRotation = step;
            UpdatePlacementGhost(player, false);
            GameObject? ghost = GetPlacementGhost(player);
            float yaw = player.m_placeRotationDegrees * step;
            addOutput($"OK: placeRotation={step} yaw={yaw.ToString("F1", CultureInfo.InvariantCulture)} ghost={GhostSummary(ghost)}");
        }

        private static void SelectPiece(string requestedPiece, bool noCost, Action<string> addOutput)
        {
            if (!TryGetBuildContext(noCost, addOutput, out Player player, out PieceTable buildPieces))
            {
                return;
            }

            if (!TryFindPiece(buildPieces, requestedPiece, out Piece piece, out string reason))
            {
                addOutput(reason);
                return;
            }

            if (!player.SetSelectedPiece(piece))
            {
                bool available = player.IsPieceAvailable(piece);
                addOutput($"ERROR: Piece was found but is not selectable. prefab={PrefabName(piece.gameObject)} display={SafeName(piece.m_name)} available={available} noCost={player.NoCostCheat()}");
                return;
            }

            UpdatePlacementGhost(player, false);
            GameObject? ghost = GetPlacementGhost(player);
            addOutput($"OK: selected prefab={PrefabName(piece.gameObject)} display={SafeName(piece.m_name)} category={piece.m_category} status={player.GetPlacementStatus()} ghost={GhostSummary(ghost)} noCost={player.NoCostCheat()} camera={CameraSummary()}");
        }

        private static void SetNoCost(bool enabled, Action<string> addOutput)
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                addOutput("ERROR: No local player found");
                return;
            }

            player.SetNoPlacementCost(enabled);
            addOutput($"OK: noCost={player.NoCostCheat()}");
        }

        private static void PrintStatus(bool updateGhost, Action<string> addOutput)
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                addOutput("ERROR: No local player found");
                return;
            }

            if (updateGhost)
            {
                UpdatePlacementGhost(player, false);
            }

            Piece? selected = player.GetSelectedPiece();
            StringBuilder sb = new();
            AppendPlacementDiagnostics(player, selected, sb);
            EmitLines(sb, addOutput);
        }

        private static void ProbePiece(string requestedPiece, bool noCost, Action<string> addOutput)
        {
            if (!PreparePiece(requestedPiece, noCost, addOutput, out Player player, out Piece piece))
            {
                return;
            }

            UpdatePlacementGhost(player, false);
            StringBuilder sb = new();
            AppendPlacementDiagnostics(player, piece, sb);
            EmitLines(sb, addOutput);
        }

        private static void ProbePieceAt(string requestedPiece, Vector3 point, bool noCost, Action<string> addOutput)
        {
            if (!PreparePiece(requestedPiece, noCost, addOutput, out Player player, out Piece piece))
            {
                return;
            }

            if (!AimPlacementRay(player, point, addOutput))
            {
                return;
            }

            UpdatePlacementGhost(player, false);
            StringBuilder sb = new();
            AppendPlacementDiagnostics(player, piece, sb);
            EmitLines(sb, addOutput);
        }

        private static void TryPlace(string requestedPiece, bool noCost, Action<string> addOutput)
        {
            if (!PreparePiece(requestedPiece, noCost, addOutput, out Player player, out Piece piece))
            {
                return;
            }

            bool placed = player.TryPlacePiece(piece);
            StringBuilder sb = new();
            sb.AppendLine($"OK: placed={placed}");
            AppendPlacementDiagnostics(player, piece, sb);
            EmitLines(sb, addOutput);
        }

        private static void TryPlaceAt(string requestedPiece, Vector3 point, bool noCost, Action<string> addOutput)
        {
            if (!PreparePiece(requestedPiece, noCost, addOutput, out Player player, out Piece piece))
            {
                return;
            }

            if (!AimPlacementRay(player, point, addOutput))
            {
                return;
            }

            bool placed = player.TryPlacePiece(piece);
            StringBuilder sb = new();
            sb.AppendLine($"OK: placed={placed}");
            AppendPlacementDiagnostics(player, piece, sb);
            EmitLines(sb, addOutput);
        }

        private static bool PreparePiece(string requestedPiece, bool noCost, Action<string> addOutput, out Player player, out Piece piece)
        {
            piece = null!;
            if (!TryGetBuildContext(noCost, addOutput, out player, out PieceTable buildPieces))
            {
                return false;
            }

            if (requestedPiece.Equals("selected", StringComparison.OrdinalIgnoreCase))
            {
                piece = player.GetSelectedPiece();
                if (piece == null)
                {
                    addOutput("ERROR: No selected build piece. Use cli_build_select first.");
                    return false;
                }

                return true;
            }

            if (!TryFindPiece(buildPieces, requestedPiece, out piece, out string reason))
            {
                addOutput(reason);
                return false;
            }

            if (!player.SetSelectedPiece(piece))
            {
                addOutput($"ERROR: Piece was found but is not selectable. prefab={PrefabName(piece.gameObject)} display={SafeName(piece.m_name)} available={player.IsPieceAvailable(piece)} noCost={player.NoCostCheat()}");
                return false;
            }

            return true;
        }

        private static bool TryGetBuildContext(bool noCost, Action<string> addOutput, out Player player, out PieceTable buildPieces)
        {
            player = Player.m_localPlayer;
            buildPieces = null!;
            if (player == null)
            {
                addOutput("ERROR: No local player found");
                return false;
            }

            if (noCost)
            {
                player.SetNoPlacementCost(true);
            }

            PieceTable? activeBuildPieces = GetBuildPieces(player);
            if (activeBuildPieces == null)
            {
                ItemDrop.ItemData rightItem = player.RightItem;
                string equipped = rightItem != null ? $"{PrefabName(rightItem.m_dropPrefab)} display={SafeName(rightItem.m_shared.m_name)}" : "none";
                addOutput($"ERROR: Player is not in build placement mode. Equip a hammer first. rightItem={equipped}");
                return false;
            }

            buildPieces = activeBuildPieces;
            return true;
        }

        private static PieceTable? GetBuildPieces(Player player)
        {
            return BuildPiecesField?.GetValue(player) as PieceTable;
        }

        private static GameObject? GetPlacementGhost(Player player)
        {
            return PlacementGhostField?.GetValue(player) as GameObject;
        }

        private static void UpdatePlacementGhost(Player player, bool flashGuardStone)
        {
            UpdatePlacementGhostMethod?.Invoke(player, new object[] { flashGuardStone });
        }

        private static bool TryFindPiece(PieceTable buildPieces, string requestedPiece, out Piece piece, out string reason)
        {
            List<Piece> matches = buildPieces.m_pieces
                .Select(gameObject => gameObject != null ? gameObject.GetComponent<Piece>() : null)
                .Where(candidate => candidate != null && MatchesPiece(candidate, requestedPiece))
                .Cast<Piece>()
                .OrderBy(candidate => ExactScore(candidate, requestedPiece))
                .ThenBy(candidate => PrefabName(candidate.gameObject), StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (matches.Count == 0)
            {
                piece = null!;
                reason = $"ERROR: No build piece matched '{requestedPiece}'";
                return false;
            }

            piece = matches[0];
            reason = "";
            return true;
        }

        private static int ExactScore(Piece piece, string requestedPiece)
        {
            string prefab = PrefabName(piece.gameObject);
            string display = SafeName(piece.m_name);
            if (prefab.Equals(requestedPiece, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            return display.Equals(requestedPiece, StringComparison.OrdinalIgnoreCase) ? 1 : 2;
        }

        private static bool MatchesPiece(Piece? piece, string filter)
        {
            if (piece == null)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(filter) || filter.Equals("*", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return PrefabName(piece.gameObject).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   SafeName(piece.m_name).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void AppendPlacementDiagnostics(Player player, Piece? selected, StringBuilder sb)
        {
            GameObject? ghost = GetPlacementGhost(player);
            Player.PlacementStatus status = player.GetPlacementStatus();
            sb.AppendLine($"OK: status={status} inPlaceMode={player.InPlaceMode()} noCost={player.NoCostCheat()}");
            sb.AppendLine($"SELECTED {PieceSummary(selected)}");
            sb.AppendLine($"GHOST {GhostSummary(ghost)}");
            sb.AppendLine($"CAMERA {CameraSummary()}");

            if (selected != null)
            {
                sb.AppendLine(BuildPieceFlags(selected));
            }

            AppendRayDiagnostics(player, selected, sb);
            AppendGhostColliderDiagnostics(ghost, sb);
            AppendGhostClippingDiagnostics(player, ghost, sb);
            AppendNearbyCharacterDiagnostics(ghost, sb);
            AppendHeightmapDiagnostics(ghost, sb);
        }

        private static string PieceSummary(Piece? piece)
        {
            if (piece == null)
            {
                return "none";
            }

            return $"prefab={PrefabName(piece.gameObject)} display={SafeName(piece.m_name)} category={piece.m_category} enabled={piece.m_enabled}";
        }

        private static string BuildPieceFlags(Piece piece)
        {
            string mustConnectTo = piece.m_mustConnectTo != null ? PrefabName(piece.m_mustConnectTo.gameObject) : "none";
            string blockers = piece.m_blockingPieces != null && piece.m_blockingPieces.Count > 0
                ? string.Join(",", piece.m_blockingPieces.Where(blockingPiece => blockingPiece != null).Select(blockingPiece => PrefabName(blockingPiece.gameObject)).Take(8))
                : "none";
            return "FLAGS " +
                   $"groundPiece={piece.m_groundPiece} groundOnly={piece.m_groundOnly} cultivatedOnly={piece.m_cultivatedGroundOnly} vegetationOnly={piece.m_vegetationGroundOnly} " +
                   $"waterPiece={piece.m_waterPiece} noInWater={piece.m_noInWater} notOnWood={piece.m_notOnWood} notOnTiltingSurface={piece.m_notOnTiltingSurface} " +
                   $"inCeilingOnly={piece.m_inCeilingOnly} notOnFloor={piece.m_notOnFloor} noClipping={piece.m_noClipping} clipGround={piece.m_clipGround} clipEverything={piece.m_clipEverything} " +
                   $"allowedInDungeons={piece.m_allowedInDungeons} onlyInBiome={piece.m_onlyInBiome} blockRadius={piece.m_blockRadius.ToString("F2", CultureInfo.InvariantCulture)} " +
                   $"blockingPieces={blockers} mustConnectTo={mustConnectTo} connectRadius={piece.m_connectRadius.ToString("F2", CultureInfo.InvariantCulture)} mustBeAboveConnected={piece.m_mustBeAboveConnected}";
        }

        private static void AppendRayDiagnostics(Player player, Piece? selected, StringBuilder sb)
        {
            if (PieceRayTestMethod == null)
            {
                sb.AppendLine("RAY unavailable");
                return;
            }

            bool water = selected != null && (selected.m_waterPiece || selected.m_noInWater);
            object?[] parameters = { Vector3.zero, Vector3.zero, null, null, null, water };
            bool hit = (bool)(PieceRayTestMethod.Invoke(player, parameters) ?? false);
            if (!hit)
            {
                sb.AppendLine($"RAY hit=False waterTest={water}");
                return;
            }

            Vector3 point = (Vector3)parameters[0]!;
            Vector3 normal = (Vector3)parameters[1]!;
            Piece? hitPiece = parameters[2] as Piece;
            Heightmap? heightmap = parameters[3] as Heightmap;
            Collider? waterSurface = parameters[4] as Collider;
            sb.AppendLine($"RAY hit=True point={FormatVector(point)} normal={FormatVector(normal)} normalY={normal.y.ToString("F3", CultureInfo.InvariantCulture)} hitPiece={PieceSummary(hitPiece)} heightmap={heightmap != null} waterSurface={waterSurface != null}");
        }

        private static bool AimPlacementRay(Player player, Vector3 point, Action<string> addOutput)
        {
            Vector3 origin = player.GetEyePoint();
            Vector3 direction = point - origin;
            if (direction.sqrMagnitude < 0.001f)
            {
                addOutput("ERROR: Aim target is too close to player eye position");
                return false;
            }

            player.AttackTowardsPlayerLookDir = true;
            player.SetLookDir(direction.normalized);
            player.FaceLookDirection();
            if (GameCamera.instance != null)
            {
                GameCamera.instance.transform.LookAt(point);
            }

            Physics.SyncTransforms();
            addOutput($"OK: buildAim point={FormatVector(point)} camera={CameraSummary()}");
            return true;
        }

        private static void AppendGhostColliderDiagnostics(GameObject? ghost, StringBuilder sb)
        {
            if (ghost == null)
            {
                sb.AppendLine("COLLIDERS none");
                return;
            }

            Collider[] colliders = ghost.GetComponentsInChildren<Collider>(true);
            List<Collider> solid = colliders.Where(collider => collider.enabled && !collider.isTrigger).ToList();
            Bounds? combined = null;
            foreach (Collider collider in solid)
            {
                if (combined == null)
                {
                    combined = collider.bounds;
                }
                else
                {
                    Bounds bounds = combined.Value;
                    bounds.Encapsulate(collider.bounds);
                    combined = bounds;
                }
            }

            string size = combined != null ? FormatVector(combined.Value.size) : "(0.00,0.00,0.00)";
            string names = string.Join(",", solid.Select(collider => $"{collider.name}:layer={LayerMask.LayerToName(collider.gameObject.layer)}").Take(8));
            sb.AppendLine($"COLLIDERS total={colliders.Length} solidEnabled={solid.Count} boundsSize={size} first={names}");
        }

        private static void AppendGhostClippingDiagnostics(Player player, GameObject? ghost, StringBuilder sb)
        {
            if (ghost == null)
            {
                sb.AppendLine("CLIPPING ghost=none");
                return;
            }

            int placeRayMask = PlaceRayMaskField?.GetValue(player) as int? ?? LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "piece_nonsolid", "terrain", "vehicle");
            Collider[] ghostColliders = ghost.GetComponentsInChildren<Collider>();
            Collider[] nearbyColliders = Physics.OverlapSphere(ghost.transform.position, 10f, placeRayMask);
            int penetrationCount = 0;
            string first = "";
            foreach (Collider ghostCollider in ghostColliders)
            {
                foreach (Collider nearbyCollider in nearbyColliders)
                {
                    if (!Physics.ComputePenetration(
                            ghostCollider,
                            ghostCollider.transform.position,
                            ghostCollider.transform.rotation,
                            nearbyCollider,
                            nearbyCollider.transform.position,
                            nearbyCollider.transform.rotation,
                            out Vector3 _,
                            out float distance))
                    {
                        continue;
                    }

                    if (distance <= 0.2f)
                    {
                        continue;
                    }

                    penetrationCount++;
                    if (first.Length == 0)
                    {
                        first = $"{ColliderSummary(ghostCollider)}->{ColliderSummary(nearbyCollider)} distance={distance.ToString("F2", CultureInfo.InvariantCulture)}";
                    }
                }
            }

            sb.AppendLine($"CLIPPING maxPenetration=0.20 invalid={penetrationCount > 0} count={penetrationCount} first={first}");
        }

        private static void AppendNearbyCharacterDiagnostics(GameObject? ghost, StringBuilder sb)
        {
            if (ghost == null)
            {
                sb.AppendLine("CHARACTER_OVERLAPS ghost=none");
                return;
            }

            Collider[] overlaps = Physics.OverlapSphere(ghost.transform.position, 2.5f);
            List<Character> characters = overlaps
                .Select(collider => collider.GetComponentInParent<Character>())
                .Where(character => character != null)
                .Distinct()
                .Cast<Character>()
                .ToList();
            string names = string.Join(",", characters.Select(character => $"{PrefabName(character.gameObject)}:{Vector3.Distance(character.transform.position, ghost.transform.position).ToString("F1", CultureInfo.InvariantCulture)}m").Take(8));
            sb.AppendLine($"CHARACTER_OVERLAPS radius=2.5 count={characters.Count} first={names}");
        }

        private static string ColliderSummary(Collider collider)
        {
            Piece? piece = collider.GetComponentInParent<Piece>();
            Character? character = collider.GetComponentInParent<Character>();
            Heightmap? heightmap = collider.GetComponent<Heightmap>();
            string owner = piece != null ? PrefabName(piece.gameObject) :
                character != null ? PrefabName(character.gameObject) :
                heightmap != null ? "Heightmap" :
                PrefabName(collider.gameObject);
            return $"{owner}/{collider.name}:layer={LayerMask.LayerToName(collider.gameObject.layer)}";
        }

        private static void AppendHeightmapDiagnostics(GameObject? ghost, StringBuilder sb)
        {
            if (ghost == null)
            {
                sb.AppendLine("HEIGHTMAP_AT_GHOST ghost=none");
                return;
            }

            Heightmap heightmap = Heightmap.FindHeightmap(ghost.transform.position);
            string biome = heightmap != null ? heightmap.GetBiome(ghost.transform.position).ToString() : "none";
            sb.AppendLine($"HEIGHTMAP_AT_GHOST found={heightmap != null} biome={biome}");
        }

        private static bool HasNoCostFlag(Terminal.ConsoleEventArgs args, int startIndex)
        {
            for (int i = startIndex; i < args.Length; i++)
            {
                if (IsNoCostToken(args[i]))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryParseVector(Terminal.ConsoleEventArgs args, int startIndex, out Vector3 point)
        {
            point = Vector3.zero;
            if (args.Length <= startIndex + 2 ||
                !float.TryParse(args[startIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) ||
                !float.TryParse(args[startIndex + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) ||
                !float.TryParse(args[startIndex + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
            {
                return false;
            }

            point = new Vector3(x, y, z);
            return true;
        }

        private static bool IsNoCostToken(string value)
        {
            return value.Equals("nocost", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("--nocost", StringComparison.OrdinalIgnoreCase);
        }

        private static string GhostSummary(GameObject? ghost)
        {
            if (ghost == null)
            {
                return "none";
            }

            return $"name={PrefabName(ghost)} activeSelf={ghost.activeSelf} activeInHierarchy={ghost.activeInHierarchy} pos={FormatVector(ghost.transform.position)} rotY={ghost.transform.eulerAngles.y.ToString("F1", CultureInfo.InvariantCulture)}";
        }

        private static string CameraSummary()
        {
            if (GameCamera.instance == null)
            {
                return "none";
            }

            Transform transform = GameCamera.instance.transform;
            return $"pos={FormatVector(transform.position)} forward={FormatVector(transform.forward)}";
        }

        private static void EmitLines(StringBuilder sb, Action<string> addOutput)
        {
            string[] lines = sb.ToString().Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in lines)
            {
                addOutput(line);
            }
        }

        private static string SafeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }

            return value.Replace(' ', '_');
        }

        private static string PrefabName(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return "null";
            }

            return Utils.GetPrefabName(gameObject);
        }

        private static string FormatVector(Vector3 value)
        {
            return $"({value.x.ToString("F2", CultureInfo.InvariantCulture)},{value.y.ToString("F2", CultureInfo.InvariantCulture)},{value.z.ToString("F2", CultureInfo.InvariantCulture)})";
        }
    }
}
