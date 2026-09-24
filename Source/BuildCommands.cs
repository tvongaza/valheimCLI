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

            _ = new Terminal.ConsoleCommand("cli_build_place_at", "Place a hammer piece at exact coordinates through the game's own placement call, with no camera and no ray: cli_build_place_at <prefab-or-name|selected> <x> <y> <z> [yaw] [nocost]", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                if (!PlacementRequest.TryParse(args.Args, snapped: false, out PlacementRequest? request, out string error))
                {
                    args.Context.AddString(error);
                    return;
                }

                PlaceAt(request!, args.Context.AddString);
            }, isCheat: true);

            _ = new Terminal.ConsoleCommand("cli_build_snap_points", "List the snap points of built pieces near a point, nearest first: cli_build_snap_points <x> <y> <z> [radius=4] [nameFilter]", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                if (!SnapPointsRequest.TryParse(args.Args, out SnapPointsRequest? request, out string error))
                {
                    args.Context.AddString(error);
                    return;
                }

                ListSnapPoints(request!, args.Context.AddString);
            });

            _ = new Terminal.ConsoleCommand("cli_build_place_snapped", "Place a hammer piece near coordinates, moved so its closest snap point meets a built piece's, as the hammer snaps: cli_build_place_snapped <prefab-or-name|selected> <x> <y> <z> [yaw] [snapRadius=0.5] [nocost]", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                if (!PlacementRequest.TryParse(args.Args, snapped: true, out PlacementRequest? request, out string error))
                {
                    args.Context.AddString(error);
                    return;
                }

                PlaceSnapped(request!, args.Context.AddString);
            }, isCheat: true);

            _ = new Terminal.ConsoleCommand("cli_piece_support_settle", "Recompute structural support now with the game's own rule, bottom-up, for the pieces this peer owns within a horizontal radius, and report each piece: cli_piece_support_settle <x> <z> [radius=10] [passes=3] [nameFilter]", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                if (!SupportSettleRequest.TryParse(args.Args, out SupportSettleRequest? request, out string error))
                {
                    args.Context.AddString(error);
                    return;
                }

                SettleSupport(request!, args.Context.AddString);
            }, isCheat: true);
        }

        /// <summary>
        /// cli_piece_support_settle. Runs WearNTear.UpdateSupport on every piece
        /// this peer owns in the column, lowest first, until a pass changes
        /// nothing or the pass limit is reached (see SupportSettle). This WRITES
        /// each owned piece's stored support, as the game's own update does, and
        /// the game may ask other peers to clear cached support on their pieces.
        /// It applies no damage: a piece reported held=False is one the game
        /// breaks when it next updates that piece's wear. Remote-owned pieces
        /// are not recomputed here; their line shows the support their owner
        /// last stored.
        /// </summary>
        private static void SettleSupport(SupportSettleRequest request, Action<string> addOutput)
        {
            if (ZNetScene.instance == null)
            {
                addOutput("ERROR: no world loaded");
                return;
            }

            float radiusSquared = request.Radius * request.Radius;
            List<WearNTear> column = new();
            foreach (WearNTear wear in WearNTear.GetAllInstances())
            {
                if (wear == null || wear.m_nview == null || !wear.m_nview.IsValid())
                {
                    continue;
                }

                Vector3 position = wear.transform.position;
                float dx = position.x - request.X, dz = position.z - request.Z;
                if (dx * dx + dz * dz <= radiusSquared)
                {
                    column.Add(wear);
                }
            }

            List<SettlePiece> keys = column
                .Select(wear => new SettlePiece(wear.transform.position.x, wear.transform.position.y, wear.transform.position.z, ZdoId(wear)))
                .ToList();
            List<WearNTear> ordered = SupportSettle.BottomUpOrder(keys).Select(index => column[index]).ToList();
            List<WearNTear> owned = ordered.Where(wear => wear.m_nview.IsOwner()).ToList();

            // UpdateSupport finds neighbours with a physics overlap; a piece
            // placed earlier in this frame is only found once transforms sync.
            Physics.SyncTransforms();
            int passes = SupportSettle.RunPasses(request.Passes, () =>
            {
                bool changed = false;
                foreach (WearNTear wear in owned)
                {
                    float before = wear.GetSupport();
                    wear.UpdateSupport();
                    changed |= SupportSettle.Changed(before, wear.GetSupport());
                }
                return changed;
            }, out bool converged);

            // Everything in the column settles; the filter chooses only what is reported.
            int reported = 0, held = 0, unheld = 0;
            foreach (WearNTear wear in ordered)
            {
                Piece? piece = wear.GetComponent<Piece>();
                if (!string.IsNullOrEmpty(request.NameFilter) &&
                    (piece == null ? PrefabName(wear.gameObject).IndexOf(request.NameFilter, StringComparison.OrdinalIgnoreCase) < 0 : !MatchesPiece(piece, request.NameFilter)))
                {
                    continue;
                }

                reported++;
                bool isHeld = wear.GetSupport() >= wear.GetMinSupport();
                if (isHeld) held++; else unheld++;
                Vector3 position = wear.transform.position;
                addOutput(SupportSettle.PieceLine(PrefabName(wear.gameObject), ZdoId(wear), position.x, position.y, position.z,
                    wear.GetSupport(), wear.GetMaxSupport(), wear.GetMinSupport(), isHeld, wear.m_nview.IsOwner()));
            }

            addOutput(SupportSettle.SummaryLine(reported, owned.Count, ordered.Count - owned.Count, held, unheld, passes, converged, request.Radius));
        }

        // A piece's origin can stand several metres from its own snap points
        // (a long beam, a large floor), so neighbours are gathered this much
        // wider than the snap radius and then filtered on the points themselves.
        private const float SnapNeighbourReach = 10f;

        // Player.PlacePiece instantiates at exactly the position it is given, so
        // the new piece is found within this distance of it.
        private const float PlacedPieceTolerance = 0.05f;

        /// <summary>
        /// cli_build_place_at. cli_build_try_place_at aims the camera and builds
        /// wherever the ray lands, so it cannot put a piece where no surface
        /// answers the ray (in mid-air, on top of another piece, behind a wall).
        /// This skips the ray and the ghost and calls Player.PlacePiece -- the
        /// call the hammer makes once its checks pass -- with the transform
        /// given, so the piece gets its creator, WearNTear.OnPlaced and every
        /// IPlaced hook exactly as a player-built one does.
        /// </summary>
        private static void PlaceAt(PlacementRequest request, Action<string> addOutput)
        {
            if (!PreparePiece(request.Piece, request.NoCost, addOutput, out Player player, out Piece piece))
            {
                return;
            }

            if (!TryPlaceFromCoordinates(player, piece, ToVector(request.Position), request.Yaw, addOutput, out _, out string summary))
            {
                return;
            }

            addOutput($"OK: placed {summary}");
        }

        /// <summary>
        /// cli_build_place_snapped: what the hammer does when it snaps. The new
        /// piece's snap points are worked out at the requested transform, the
        /// closest pair with a built neighbour's points is found (the rule in
        /// Player.FindClosestSnapPoints, see SnapGeometry), and the piece is
        /// moved by that pair's offset before it is placed. Pieces placed at
        /// bare coordinates stand side by side without meeting; snapped ones
        /// meet exactly, which is what makes them one connected structure.
        /// </summary>
        private static void PlaceSnapped(PlacementRequest request, Action<string> addOutput)
        {
            if (!PreparePiece(request.Piece, request.NoCost, addOutput, out Player player, out Piece piece))
            {
                return;
            }

            List<Transform> ownPoints = new();
            piece.GetSnapPoints(ownPoints);
            if (ownPoints.Count == 0)
            {
                addOutput($"ERROR: code=no_snap_points prefab={PrefabName(piece.gameObject)} has no snap points; use cli_build_place_at");
                return;
            }

            // Worked out from the prefab rather than from the placement ghost,
            // which the game moves every frame to follow the camera.
            Point3 rootScale = ToPoint(piece.transform.localScale);
            List<Point3> mine = ownPoints
                .Select(point => SnapGeometry.ChildToWorld(request.Position, request.Yaw, rootScale, ToPoint(point.localPosition)))
                .ToList();
            List<(Piece Owner, Transform Point)> neighbours = GatherWorldSnapPoints(ToVector(request.Position), request.SnapRadius + SnapNeighbourReach, "");
            List<Point3> theirs = neighbours.Select(entry => ToPoint(entry.Point.position)).ToList();

            if (!SnapGeometry.TryClosestPair(mine, theirs, request.SnapRadius, out SnapMatch match, out float nearestGap))
            {
                string nearest = float.IsInfinity(nearestGap) ? "none" : F3(nearestGap);
                addOutput($"ERROR: code=no_snap_point snapRadius={F3(request.SnapRadius)} candidates={theirs.Count} nearestGap={nearest}; cli_build_snap_points lists what is there");
                return;
            }

            Point3 offset = theirs[match.Theirs] - mine[match.Mine];
            if (!TryPlaceFromCoordinates(player, piece, ToVector(request.Position + offset), request.Yaw, addOutput, out Piece placed, out string summary))
            {
                return;
            }

            // Measured on the placed piece, not predicted: the distance between
            // its snap point and the neighbour's after it was built.
            List<Transform> placedPoints = new();
            placed.GetSnapPoints(placedPoints);
            Transform theirPoint = neighbours[match.Theirs].Point;
            string gapAfter = match.Mine < placedPoints.Count ? F3(Vector3.Distance(placedPoints[match.Mine].position, theirPoint.position)) : "unknown";
            Piece owner = neighbours[match.Theirs].Owner;

            addOutput($"OK: placed {summary}");
            addOutput($"SNAP snappedTo={PrefabName(owner.gameObject)} snappedToZdo={ZdoId(owner)} theirPoint={theirs[match.Theirs].Format()} myPoint={mine[match.Mine].Format()} " +
                      $"requested={request.Position.Format()} offset={offset.Format()} gapBefore={F3(match.Gap)} gapAfter={gapAfter} candidates={theirs.Count}");
        }

        /// <summary>
        /// The shared half of both coordinate placements: the position rules,
        /// the game's own placement call, then the hammer's cost. What the
        /// hammer decides on its camera-driven ghost -- clipping, a player in
        /// the way, room to stand, ground type -- is not applied; nor are
        /// stamina, tool durability, skill gain or the build statistics.
        /// </summary>
        private static bool TryPlaceFromCoordinates(Player player, Piece piece, Vector3 position, float yaw, Action<string> addOutput, out Piece placed, out string summary)
        {
            placed = null!;
            summary = "";
            string prefab = PrefabName(piece.gameObject);
            Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);

            // The hammer will not snap a piece onto the same piece standing in
            // the same place. Both commands apply that rule, so a script that
            // runs twice does not stack duplicates.
            List<Piece> occupants = new();
            Piece.GetAllPiecesInRadius(position, PlacedPieceTolerance, occupants);
            foreach (Piece occupant in occupants)
            {
                if (occupant != null && PrefabName(occupant.gameObject) == prefab &&
                    SnapGeometry.OverlapsSamePiece(Vector3.Distance(occupant.transform.position, position), Quaternion.Angle(occupant.transform.rotation, rotation), piece.m_allowRotatedOverlap))
                {
                    addOutput($"ERROR: code=occupied prefab={prefab} zdo={ZdoId(occupant)} already stands at {FormatVector3(position)}");
                    return false;
                }
            }

            PrivateArea? ward = piece.GetComponent<PrivateArea>();
            string? refusal = PlacementRules.Refusal(
                ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(position, out float _),
                Location.IsInsideNoBuildLocation(position),
                !PrivateArea.CheckAccess(position, ward != null ? ward.m_radius : 0f, false, ward != null),
                piece.m_onlyInBiome != Heightmap.Biome.None && (Heightmap.FindBiome(position) & piece.m_onlyInBiome) == 0,
                !player.NoCostCheat() && !player.HaveRequirements(piece, Player.RequirementMode.CanBuild));
            if (refusal != null)
            {
                addOutput($"ERROR: code={refusal} prefab={prefab} at={FormatVector(position)} noCost={player.NoCostCheat()}");
                return false;
            }

            List<Piece> before = new();
            Piece.GetAllPiecesInRadius(position, PlacedPieceTolerance, before);
            HashSet<Piece> existing = new(before);

            // Marked as cheated on the same terms the hammer uses.
            bool cheated = (player.GetInventory().ItemCheated(piece.m_resources) || player.NoCostCheat()) && !PlayerProfile.s_bypassCheatChecks;
            player.PlacePiece(piece, position, rotation, false, cheated);

            // PlacePiece returns nothing, so the result is the new piece itself,
            // found by identity: one that was not standing here before.
            List<Piece> after = new();
            Piece.GetAllPiecesInRadius(position, PlacedPieceTolerance, after);
            Piece? created = after.FirstOrDefault(candidate => candidate != null && !existing.Contains(candidate) && PrefabName(candidate.gameObject) == prefab);
            if (created == null)
            {
                addOutput($"ERROR: code=not_placed prefab={prefab} at={FormatVector(position)}: no new piece stands there after the placement call");
                return false;
            }

            // The hammer takes the resources after a successful placement unless
            // the world's free-build key is set -- in no-cost mode too, where it
            // takes whatever of them the inventory holds.
            bool freeBuild = ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(piece.FreeBuildKey());
            if (!freeBuild)
            {
                player.ConsumeResources(piece.m_resources, 0);
            }

            placed = created;
            int? step = SnapGeometry.HammerStep(yaw);
            summary = $"prefab={prefab} zdo={ZdoId(created)} at={FormatVector3(created.transform.position)} yaw={yaw.ToString("F1", CultureInfo.InvariantCulture)} " +
                      $"hammerStep={(step.HasValue ? step.Value.ToString(CultureInfo.InvariantCulture) : "none")} cheated={cheated} noCost={player.NoCostCheat()} freeBuild={freeBuild}";
            return true;
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

        /// <summary>
        /// Every snap point of every built piece within a radius, in world space,
        /// asking each piece for its own points. The game's static helper finds
        /// neighbours through a physics overlap, which misses a piece placed
        /// earlier in the same frame until the physics scene syncs; the piece
        /// list does not.
        /// </summary>
        private static List<(Piece Owner, Transform Point)> GatherWorldSnapPoints(Vector3 centre, float radius, string nameFilter)
        {
            List<(Piece, Transform)> found = new();
            List<Piece> pieces = new();
            Piece.GetAllPiecesInRadius(centre, radius + SnapNeighbourReach, pieces);
            List<Transform> points = new();
            foreach (Piece owner in pieces)
            {
                if (owner == null || (!string.IsNullOrEmpty(nameFilter) && !MatchesPiece(owner, nameFilter)))
                {
                    continue;
                }

                points.Clear();
                owner.GetSnapPoints(points);
                foreach (Transform point in points)
                {
                    if (point != null && Vector3.Distance(point.position, centre) <= radius)
                    {
                        found.Add((owner, point));
                    }
                }
            }

            return found;
        }

        private static void ListSnapPoints(SnapPointsRequest request, Action<string> addOutput)
        {
            Vector3 centre = ToVector(request.Centre);
            List<(Piece Owner, Transform Point)> points = GatherWorldSnapPoints(centre, request.Radius, request.NameFilter);
            StringBuilder sb = new();
            foreach ((Piece owner, Transform point) in points.OrderBy(entry => Vector3.Distance(entry.Point.position, centre)))
            {
                sb.AppendLine($"SNAP piece={PrefabName(owner.gameObject)} zdo={ZdoId(owner)} point={FormatVector3(point.position)} dist={F3(Vector3.Distance(point.position, centre))}");
            }

            sb.Append($"OK: snapPoints={points.Count} centre={request.Centre.Format()} radius={F3(request.Radius)} filter='{request.NameFilter}'");
            EmitLines(sb, addOutput);
        }

        private static string ZdoId(Component component)
        {
            ZNetView? view = component.GetComponent<ZNetView>();
            ZDO? zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            return zdo != null ? zdo.m_uid.ToString() : "none";
        }

        private static Vector3 ToVector(Point3 point) => new(point.X, point.Y, point.Z);

        private static Point3 ToPoint(Vector3 vector) => new(vector.x, vector.y, vector.z);

        private static string F3(float value) => value.ToString("F3", CultureInfo.InvariantCulture);

        private static string FormatVector3(Vector3 value) => ToPoint(value).Format();

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
