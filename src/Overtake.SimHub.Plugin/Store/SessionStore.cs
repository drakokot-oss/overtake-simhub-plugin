using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Overtake.SimHub.Plugin.Finalizer;
using Overtake.SimHub.Plugin.Packets;
using Overtake.SimHub.Plugin.Parsers;

namespace Overtake.SimHub.Plugin.Store
{
    public class SessionStore
    {
        private const int MaxEventsPerSession = 8000;
        private const int MaxPitEventsPerDriver = 50;

        private const int TAG_RELIABLE = 2;
        private const int TAG_UNRELIABLE = 1;
        private const int TAG_GENERIC = 0;

        public bool Connected;
        public ulong? SessionUid;
        // Live broadcast binding (set when the session went on air via the portal flow).
        // Stamped into the OTK `capture` block so the site can auto-link the file to the race.
        public string LiveRaceId;
        public string LiveLeagueId;
        public string LiveGridId;
        public string LiveBroadcastSessionId;
        public long StartedAtMs;
        public long LastPacketMs;
        public Dictionary<int, int> PacketCounts = new Dictionary<int, int>();
        public List<string> Notes = new List<string>();
        public Dictionary<string, SessionRun> Sessions = new Dictionary<string, SessionRun>();

        // ----------------------------------------------------------------------
        // Raw packet sampling (Phase 2 enabler — v1.1.39)
        // ----------------------------------------------------------------------
        // When the UDP wire format is one the parsers do NOT support yet (e.g.
        // 2026), the parsed bodies are garbage. To enable reverse-engineering the
        // new layout WITHOUT shipping a separate tool, we capture ONE raw sample
        // per packetId (first occurrence) the moment an unsupported format is
        // observed: format + total length + a capped hex prefix. LeagueFinalizer
        // emits these under _debug.rawSamples so a single labeled 2026 capture
        // carries everything needed to map the new offsets. Bounded by design:
        // one entry per packetId, RawSampleHexCap bytes each.
        //
        // v1.1.40 — raised 256 -> 2048 so a single sample now covers the FULL
        // body of every F1 packet (largest is ~1470 B). This lets us map the
        // deep Session fields (lobby settings / assists at offset ~639+) and the
        // LobbyInfo packet, which the previous 256-byte cap could not reach.
        // Cost: ~one extra packet's worth of hex per packetId, only present in
        // captures that used an unsupported wire format (never in normal 2025).
        public const int RawSampleHexCap = 2048;
        /// <summary>packetId -> { format, length, hex } captured under an unsupported wire format.</summary>
        public Dictionary<int, Dictionary<string, object>> RawSamples = new Dictionary<int, Dictionary<string, object>>();
        /// <summary>Highest non-zero PacketFormat seen that the parsers do NOT support (0 = none).</summary>
        public ushort UnsupportedFormatSeen;
        /// <summary>
        /// v1.1.43 — latest full Session-packet raw bytes when the wire format's
        /// deep Session fields are not mapped yet (2026). Used to reverse-engineer
        /// the lobby-settings offsets from a packet that already has them loaded.
        /// Null otherwise. Emitted as _debug.sessionDeepProbe.
        /// </summary>
        public Dictionary<string, object> SessionDeepProbe;

        // Cross-session name resolution: "raceNumber_teamId" -> real name
        private Dictionary<string, string> _bestKnownTags = new Dictionary<string, string>();
        private Dictionary<string, int> _bestKnownTagReliability = new Dictionary<string, int>();

        // Network-id-based name resolution: "net{networkId}_{teamId}" -> real name.
        // Primary disambiguator for raceNumber collisions in Custom MyTeam lobbies
        // (issue #1: m_raceNumber duplicates across players is a known F1 24/25 bug).
        // Only populated for confirmed network humans (NetworkId != 255 && !aiControlled).
        private Dictionary<string, string> _bestKnownTagsByNet = new Dictionary<string, string>();
        private Dictionary<string, int> _bestKnownTagReliabilityByNet = new Dictionary<string, int>();

        // raceNumber_teamId keys that are known to be shared by 2+ distinct network humans
        // in this session. Lookups via the rn-keyed map skip these to avoid name-stealing.
        private HashSet<string> _rnKeyAmbiguous = new HashSet<string>();

        // LobbyInfo name lookup: "raceNumber_teamId" -> name (including generic names).
        // LobbyInfo slot index does NOT correspond to in-session carIdx, so we
        // must NOT use it for carIdx-based seeding. Instead we match via (teamId, raceNumber).
        private Dictionary<string, string> _lobbyNameByTeamRn = new Dictionary<string, string>();
        // Fallback: teamId-only → name. Only used when (raceNumber,teamId) key fails.
        // Stores only NON-generic names. If multiple players share a teamId, value = null (ambiguous).
        private Dictionary<int, string> _lobbyNameByTeamOnly = new Dictionary<int, string>();
        // Track distinct lookup_keys per teamId to detect shared teams.
        private Dictionary<int, HashSet<string>> _lobbyTeamKeys = new Dictionary<int, HashSet<string>>();
        public int LobbyNumPlayers;

        public Dictionary<string, string> DebugLobbyMap { get { return new Dictionary<string, string>(_lobbyNameByTeamRn); } }
        public Dictionary<string, string> DebugBestKnownTags { get { return new Dictionary<string, string>(_bestKnownTags); } }
        public Dictionary<string, string> DebugBestKnownTagsByNet { get { return new Dictionary<string, string>(_bestKnownTagsByNet); } }
        public List<string> DebugRnKeyAmbiguous { get { return new List<string>(_rnKeyAmbiguous); } }
        public Dictionary<string, string> DebugLobbyByTeamOnly
        {
            get
            {
                var d = new Dictionary<string, string>();
                foreach (var kvp in _lobbyNameByTeamOnly)
                    d[kvp.Key.ToString()] = kvp.Value ?? "(ambiguous)";
                return d;
            }
        }

        // Diagnostics
        public int DiagLobbyResolved;
        public int DiagLobbyFailed;
        public int DiagShReceived;
        public int DiagShNoDriver;
        public int DiagShDedup;
        public int DiagShLapsParsed;
        public int DiagShLapsAccepted;
        public int DiagShLapsFiltered;
        public int DiagLdLapRecorded;
        public int DiagLdNoDriver;
        public int DiagLdNoPrevLap;
        public int DiagLdTimeZero;
        public int DiagLdAlreadyExists;
        public int DiagLdSanityFail;
        public int DiagLdEarlyRegister;
        public int DiagShEarlyRegister;
        public int DiagParticipantsReceived;
        public int DiagParticipantsNumActive;
        public int DiagPlayerCarIdx = -1;
        public int DiagPlayerRecoveredFromOverflow;
        public int DiagFcTotal;
        public int DiagFcRegistered;
        public int DiagLobbyInfoReceived;
        public int DiagLobbyInfoPlayers;

        // Full My Team (all active slots MyTeam=true, online): lobby wins on raceNumber_teamId keys.
        private bool _captureFullMyTeam;
        private int _fullMyTeamStreak;
        public bool CaptureFullMyTeam { get { return _captureFullMyTeam; } }

        /// <summary>
        /// When the UDP header says 2026 but the payload body uses the legacy 2025
        /// stride (My Team online), this is pinned to 2025 after the first probeable
        /// packet. Null until resolved; 2026 for true Season Pack grids.
        /// </summary>
        public ushort? ResolvedBodyWireFormat { get; private set; }

        /// <summary>
        /// Parses a raw UDP packet using the sticky body layout for this capture.
        /// </summary>
        public ParsedPacket ParsePacket(byte[] raw)
        {
            var parsed = Parsers.PacketParser.Dispatch(raw, ResolvedBodyWireFormat);
            if (parsed?.Header != null
                && parsed.Header.PacketFormat >= 2026
                && !ResolvedBodyWireFormat.HasValue)
            {
                ushort? probed = Packets.WireLayoutProbe.TryProbe(raw, parsed.Header.PacketId);
                if (probed.HasValue)
                    ResolvedBodyWireFormat = probed;
            }
            return parsed;
        }

        /// <summary>Populated at export: lobby vs bestKnown disagreements before full-My-Team merge.</summary>
        public List<Dictionary<string, object>> LastExportedNameKeyConflicts { get; set; }

        private static readonly HashSet<string> IgnoreEvents =
            new HashSet<string> { "SPTP", "DRSE", "DRSD", "STLG", "BUTN" };

        // Track the last-known trackId to detect lobby changes.
        // When trackId changes, we clear cross-session name caches because
        // raceNumber_teamId keys can be reused by different players in different lobbies.
        private int? _lastTrackId;

        // Camada 1 — Auto-rotation signal for the OvertakePlugin layer.
        // Set when a Session packet for a NEW trackId arrives AFTER the current capture
        // already contains a closed terminal session (Race with FinalClassification).
        // OvertakePlugin sees this in DataUpdate, exports the closed capture, then calls
        // BeginNewCapture so the new track's data lands in a fresh store.
        public bool AutoRotateRequested { get; private set; }
        public string AutoRotateReason { get; private set; }

        public void ClearAutoRotateRequest()
        {
            AutoRotateRequested = false;
            AutoRotateReason = null;
        }

        public SessionStore()
        {
            StartedAtMs = NowMs();
            LastExportedNameKeyConflicts = new List<Dictionary<string, object>>();
        }

        /// <summary>
        /// Clears all captured sessions, per-driver data, and cross-session name caches.
        /// UDP listener is unchanged. Use after export when starting another race/lobby
        /// so the next file is not merged with or polluted by the previous capture.
        /// </summary>
        public void BeginNewCapture()
        {
            Sessions.Clear();
            _bestKnownTags.Clear();
            _bestKnownTagReliability.Clear();
            _bestKnownTagsByNet.Clear();
            _bestKnownTagReliabilityByNet.Clear();
            _rnKeyAmbiguous.Clear();
            _lobbyNameByTeamRn.Clear();
            _lobbyNameByTeamOnly.Clear();
            _lobbyTeamKeys.Clear();
            _lastTrackId = null;
            _captureFullMyTeam = false;
            _fullMyTeamStreak = 0;
            ResolvedBodyWireFormat = null;
            SessionUid = null;
            LiveRaceId = null;
            LiveLeagueId = null;
            LiveGridId = null;
            LiveBroadcastSessionId = null;
            StartedAtMs = NowMs();
            LastPacketMs = 0;
            PacketCounts.Clear();
            Notes.Clear();
            RawSamples.Clear();
            UnsupportedFormatSeen = 0;
            SessionDeepProbe = null;
            LastExportedNameKeyConflicts.Clear();
            LobbyNumPlayers = 0;

            DiagLobbyResolved = 0;
            DiagLobbyFailed = 0;
            DiagShReceived = 0;
            DiagShNoDriver = 0;
            DiagShDedup = 0;
            DiagShLapsParsed = 0;
            DiagShLapsAccepted = 0;
            DiagShLapsFiltered = 0;
            DiagLdLapRecorded = 0;
            DiagLdNoDriver = 0;
            DiagLdNoPrevLap = 0;
            DiagLdTimeZero = 0;
            DiagLdAlreadyExists = 0;
            DiagLdSanityFail = 0;
            DiagLdEarlyRegister = 0;
            DiagShEarlyRegister = 0;
            DiagParticipantsReceived = 0;
            DiagParticipantsNumActive = 0;
            DiagPlayerCarIdx = -1;
            DiagPlayerRecoveredFromOverflow = 0;
            DiagFcTotal = 0;
            DiagFcRegistered = 0;
            DiagLobbyInfoReceived = 0;
            DiagLobbyInfoPlayers = 0;

            ClearAutoRotateRequest();
        }

        /// <summary>
        /// Returns true when the current capture already contains at least one Race-style
        /// session that received FinalClassification (i.e. the previous event is closed).
        /// Used by the auto-rotation logic to decide whether to split captures on track change.
        /// </summary>
        public bool HasClosedTerminalSession()
        {
            var all = Sessions.Values;
            foreach (var sess in all)
            {
                if (Finalizer.SprintFormatHelper.IsTerminalRaceSession(sess, all))
                    return true;
            }
            return false;
        }

        private static long NowMs()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        /// <summary>
        /// Detect lobby change: when trackId changes, clear all cross-session name caches.
        /// raceNumber_teamId keys are reused by different players in different lobbies,
        /// so stale entries would cause wrong names.
        /// </summary>
        private void CheckLobbyChange(int newTrackId)
        {
            if (_lastTrackId.HasValue && _lastTrackId.Value != newTrackId)
            {
                if (Notes.Count < 500)
                    Notes.Add(string.Format("LOBBY CHANGE detected: trackId {0}->{1}. Clearing name caches.",
                        _lastTrackId.Value, newTrackId));
                _bestKnownTags.Clear();
                _bestKnownTagReliability.Clear();
                _bestKnownTagsByNet.Clear();
                _bestKnownTagReliabilityByNet.Clear();
                _rnKeyAmbiguous.Clear();
                _lobbyNameByTeamRn.Clear();
                _lobbyNameByTeamOnly.Clear();
                _lobbyTeamKeys.Clear();
                DiagLobbyResolved = 0;
                DiagLobbyFailed = 0;
                _captureFullMyTeam = false;
                _fullMyTeamStreak = 0;
                ResolvedBodyWireFormat = null;
            }
            _lastTrackId = newTrackId;
        }

        /// <summary>
        /// All active slots are My Team cars (online). Official grids have MyTeam=false.
        /// v1.1.47: the m_myTeam flag is unreliable on F1 26 captures (reads 0 for every
        /// car, including the player's), so a slot also counts as My Team when its teamId
        /// is a known My Team id (41/104/232). Without this, real F1 26 full-My-Team
        /// league lobbies (full grid of teamId 232, all myTeam=false) were never detected.
        /// </summary>
        private static bool DetectFullMyTeamGrid(SessionRun sess, ParticipantEntry[] entries, int numActive)
        {
            if (sess.NetworkGame != 1 || numActive < 2) return false;
            if (entries == null) return false;
            for (int i = 0; i < numActive && i < entries.Length; i++)
            {
                var e = entries[i];
                if (e == null) return false;
                if (e.TeamId == 255) return false;
                if (!e.MyTeam && !Finalizer.Lookups.IsMyTeamTeamId(e.TeamId)) return false;
            }
            return true;
        }

        /// <summary>
        /// Before finalizing export: make lobby roster win every raceNumber_teamId seat (full My Team only).
        /// </summary>
        public void ApplyFullMyTeamLobbyMergeIfNeeded()
        {
            if (!_captureFullMyTeam) return;
            foreach (var kv in _lobbyNameByTeamRn)
            {
                string lv = kv.Value;
                if (string.IsNullOrEmpty(lv) || IsGenericTag(lv)) continue;
                string bk;
                if (!_bestKnownTags.TryGetValue(kv.Key, out bk) || IsGenericTag(bk) || bk != lv)
                {
                    _bestKnownTags[kv.Key] = lv;
                    int rel;
                    if (!_bestKnownTagReliability.TryGetValue(kv.Key, out rel) || rel < TAG_RELIABLE)
                        _bestKnownTagReliability[kv.Key] = TAG_RELIABLE;
                }
            }
        }

        /// <summary>Conflicts between lobby map and bestKnownTags before merge (diagnostics).</summary>
        public List<Dictionary<string, object>> SnapshotNameKeyConflicts()
        {
            var rows = new List<Dictionary<string, object>>();
            foreach (var kv in _lobbyNameByTeamRn)
            {
                string lv = kv.Value;
                if (string.IsNullOrEmpty(lv) || IsGenericTag(lv)) continue;
                string bk;
                if (_bestKnownTags.TryGetValue(kv.Key, out bk) && !string.IsNullOrEmpty(bk) && !IsGenericTag(bk) && bk != lv)
                    rows.Add(new Dictionary<string, object> {
                        { "key", kv.Key },
                        { "lobbyName", lv },
                        { "bestKnown", bk },
                    });
            }
            return rows;
        }

        private static string MsToStr(int ms)
        {
            if (ms <= 0) return "";
            double total = ms / 1000.0;
            int m = (int)(total / 60.0);
            double s = total - 60.0 * m;
            return string.Format("{0}:{1:00.000}", m, s);
        }

        private string GetSessionKey(PacketHeader header)
        {
            ulong sidVal = header.SessionUid;
            if (sidVal == 0 && SessionUid.HasValue)
                sidVal = SessionUid.Value;
            string sid = sidVal.ToString();
            if (!Sessions.ContainsKey(sid))
            {
                var newSess = new SessionRun { SessionUID = sid };
                // Cross-session carry-over from the most recent previous session.
                // Only within the SAME lobby (same trackId). Across different lobbies,
                // carIdx slots are used by different players, so carrying names would
                // assign the wrong gamertag to the wrong car.
                if (Sessions.Count > 0)
                {
                    SessionRun prevSess = null;
                    foreach (var kvp in Sessions)
                        prevSess = kvp.Value;
                    if (prevSess != null)
                    {
                        bool sameLobby = _lastTrackId.HasValue && prevSess.TrackId.HasValue
                            && prevSess.TrackId.Value == _lastTrackId.Value;
                        // v2.0.2 — F1 26 REMAPS carIdx between the parts of a 3-part
                        // qualifying (Q1/Q2/Q3 = sessionType 5/6/7). A named carIdx→tag
                        // learned in one part is therefore NOT reliable for the next
                        // session (the same slot may now be a different driver), and
                        // carrying it forward mis-attributes lap history to the wrong
                        // car (the "Car_9/Eduquepro get Quintino's laps" bug). So when
                        // we're LEAVING a multi-part-qualy part, carry ONLY generic
                        // placeholders (like the cross-lobby case) and let the new
                        // part's Participants packet rebuild the real names from scratch.
                        bool prevIsQualyPart = prevSess.SessionType.HasValue
                            && prevSess.SessionType.Value >= 5 && prevSess.SessionType.Value <= 7;
                        bool carryNamed = sameLobby && !prevIsQualyPart;
                        foreach (var kvp in prevSess.TagsByCarIdx)
                        {
                            if (carryNamed)
                            {
                                newSess.TagsByCarIdx[kvp.Key] = kvp.Value;
                            }
                            else
                            {
                                // Different lobby OR leaving a qualy part: only carry
                                // generic placeholders — named tags belong to a carIdx
                                // that may now hold a different player.
                                if (IsGenericTag(kvp.Value))
                                    newSess.TagsByCarIdx[kvp.Key] = kvp.Value;
                            }
                        }
                        foreach (var kvp in prevSess.TeamByCarIdx)
                        {
                            if (carryNamed)
                                newSess.TeamByCarIdx[kvp.Key] = kvp.Value;
                            // Don't carry team data across lobbies / qualy-part remaps:
                            // a different player may be at the same carIdx with a different team.
                        }
                        if (prevSess.MaxNumActiveCars > newSess.MaxNumActiveCars)
                            newSess.MaxNumActiveCars = prevSess.MaxNumActiveCars;
                        if (carryNamed)
                        {
                            foreach (var kvp in prevSess.TagReliability)
                                newSess.TagReliability[kvp.Key] = kvp.Value;
                            foreach (var kvp in prevSess.HumanCarIdxs)
                                newSess.HumanCarIdxs[kvp.Key] = kvp.Value;
                        }
                    }
                }
                Sessions[sid] = newSess;
            }
            return sid;
        }

        private DriverRun EnsureDriver(string sid, int carIdx)
        {
            var sess = Sessions[sid];
            string tag;
            if (!sess.TagsByCarIdx.TryGetValue(carIdx, out tag) || tag == null)
                return null;
            DriverRun dr;
            if (!sess.Drivers.TryGetValue(tag, out dr))
            {
                dr = new DriverRun { Tag = tag, CarIdx = carIdx };
                sess.Drivers[tag] = dr;
            }
            return dr;
        }

        /// <summary>
        /// Register a placeholder driver when data arrives before the Participants packet.
        /// Uses "Car_X" prefix so it's distinct from the game's "Player" dedup naming.
        /// Returns the DriverRun if registration succeeded, null if the slot is empty.
        /// </summary>
        private DriverRun EarlyRegisterDriver(string sid, SessionRun sess, int carIdx)
        {
            if (carIdx < 0 || carIdx >= 22) return null;
            string placeholder = "Car_" + carIdx;
            sess.TagsByCarIdx[carIdx] = placeholder;
            return EnsureDriver(sid, carIdx);
        }

        /// <summary>
        /// Main entry point: ingests a parsed packet into the accumulated state.
        /// Called from OvertakePlugin.DataUpdate (SimHub main thread, ~60Hz).
        /// </summary>
        public void Ingest(ParsedPacket parsed)
        {
            if (parsed == null || parsed.Header == null) return;

            long nowMs = NowMs();
            LastPacketMs = nowMs;
            Connected = true;
            var header = parsed.Header;

            // Camada 1 — Auto-rotation guard:
            // Step 1: when a Session packet announces a NEW trackId AND the current capture
            // already has a closed Race, raise the rotation flag BEFORE any new-event data
            // leaks into the old store.
            if (!AutoRotateRequested
                && header.PacketId == 1
                && parsed.Session != null
                && parsed.Session.TrackId >= 0
                && _lastTrackId.HasValue
                && _lastTrackId.Value != parsed.Session.TrackId
                && HasClosedTerminalSession())
            {
                AutoRotateRequested = true;
                AutoRotateReason = string.Format(
                    "trackId {0}->{1} after closed race",
                    _lastTrackId.Value, parsed.Session.TrackId);
                if (Notes.Count < 500)
                    Notes.Add(string.Format(
                        "AUTO-ROTATE requested: {0}. Plugin will export old capture and start fresh.",
                        AutoRotateReason));
            }

            // Step 2: while a rotation is pending, drop EVERY packet so the new event
            // doesn't pollute the closed capture. OvertakePlugin's DataUpdate sees the flag
            // after this DataUpdate's dequeue loop ends and triggers export+BeginNewCapture,
            // clearing the flag. The next DataUpdate iteration ingests the new event cleanly.
            if (AutoRotateRequested)
                return;

            if (header.SessionUid != 0)
            {
                if (!SessionUid.HasValue)
                {
                    SessionUid = header.SessionUid;
                }
                else if (header.SessionUid != SessionUid.Value)
                {
                    if (Notes.Count < 500)
                        Notes.Add(string.Format("sessionUID changed: {0} -> {1}", SessionUid.Value, header.SessionUid));
                    SessionUid = header.SessionUid;
                }
            }

            int pid = header.PacketId;
            if (PacketCounts.ContainsKey(pid))
                PacketCounts[pid]++;
            else
                PacketCounts[pid] = 1;

            // Phase 2 enabler (v1.1.39): if this packet's wire format is one the
            // parsers do NOT support, the parsed body above is unreliable. Capture
            // a single raw sample per packetId so the layout can be mapped offline.
            // No-op for the supported 2025 format (the overwhelming common case).
            if (!Packets.GameInfo.IsParseSupportedFormat(header.PacketFormat))
            {
                if (header.PacketFormat > UnsupportedFormatSeen)
                    UnsupportedFormatSeen = header.PacketFormat;
                if (!RawSamples.ContainsKey(pid) && parsed.RawData != null)
                {
                    int n = Math.Min(RawSampleHexCap, parsed.RawData.Length);
                    var sb = new StringBuilder(n * 2);
                    for (int b = 0; b < n; b++)
                        sb.Append(parsed.RawData[b].ToString("x2"));
                    RawSamples[pid] = new Dictionary<string, object>
                    {
                        { "packetFormat", (int)header.PacketFormat },
                        { "length", parsed.RawData.Length },
                        { "hexPrefix", sb.ToString() },
                    };
                    if (Notes.Count < 500)
                        Notes.Add(string.Format(
                            "RAW SAMPLE captured: packetId={0} format={1} len={2} (unsupported wire format; see _debug.rawSamples)",
                            pid, header.PacketFormat, parsed.RawData.Length));
                }
            }

            // v1.1.43 — Session deep-field probe. The deep Session block (lobby
            // settings / assists, payload offset ~639+) is NOT mapped for the 2026
            // wire format yet, so we omit lobbySettings there. To finish the map we
            // need a LATE Session packet (settings load AFTER the first one, which
            // ships them zeroed). The general raw sampler only keeps the FIRST
            // packet, so here we keep the LATEST Session packet (overwrite) whenever
            // the format's deep fields are unmapped. Self-disables once 2026 deep
            // fields become mapped (AreDeepSessionFieldsMapped). Diagnostic only —
            // does NOT flag the file (unsupportedUdpFormat stays null) so 2026
            // imports keep working while we gather this.
            if (pid == 1 && parsed.RawData != null
                && !Packets.GameInfo.AreDeepSessionFieldsMapped(header.PacketFormat))
            {
                int n = Math.Min(RawSampleHexCap, parsed.RawData.Length);
                var sb = new StringBuilder(n * 2);
                for (int b = 0; b < n; b++)
                    sb.Append(parsed.RawData[b].ToString("x2"));
                SessionDeepProbe = new Dictionary<string, object>
                {
                    { "packetFormat", (int)header.PacketFormat },
                    { "length", parsed.RawData.Length },
                    { "hexPrefix", sb.ToString() },
                };
            }

            string sid = GetSessionKey(header);
            var sess = Sessions[sid];
            sess.LastPacketMs = nowMs;

            // Track player car index (255 = invalid/spectator)
            if (header.PlayerCarIndex < 255)
                sess.PlayerCarIndex = header.PlayerCarIndex;

            // v1.1.36 — Capture game-version markers from every packet header.
            // We overwrite on each packet on purpose: if the game restarts mid-
            // session (rare), the most-recent values reflect the active build.
            // Zero PacketFormat is treated as "not yet observed" downstream so a
            // malformed first packet won't pin the session to "F1_0".
            if (header.PacketFormat != 0)
            {
                sess.LastPacketFormat = header.PacketFormat;
                sess.LastGameYear = header.GameYear;
                sess.LastGameMajorVersion = header.GameMajorVersion;
                sess.LastGameMinorVersion = header.GameMinorVersion;
            }

            // 0) Motion (Track Map — live UI only)
            if (pid == 0 && parsed.Motion != null)
                IngestMotion(sid, parsed.Motion);

            // 6) Car Telemetry (tyre/brake/engine temps — live UI only)
            else if (pid == 6 && parsed.CarTelemetry != null)
                IngestCarTelemetry(sid, parsed.CarTelemetry);

            // 1) Session
            else if (pid == 1 && parsed.Session != null)
                IngestSession(sess, parsed.Session, nowMs);

            // 4) Participants
            else if (pid == 4 && parsed.Participants != null)
                IngestParticipants(sid, sess, parsed.Participants, header.PlayerCarIndex);

            // 9) LobbyInfo
            else if (pid == 9 && parsed.LobbyInfo != null)
                IngestLobbyInfo(sid, sess, parsed.LobbyInfo);

            // 3) Event
            else if (pid == 3 && parsed.Event != null)
                IngestEvent(sid, sess, parsed.Event, nowMs);

            // 11) SessionHistory
            else if (pid == 11 && parsed.SessionHistory != null)
                IngestSessionHistory(sid, sess, parsed.SessionHistory, nowMs);

            // 8) FinalClassification
            else if (pid == 8 && parsed.FinalClassification != null)
                IngestFinalClassification(sess, parsed.FinalClassification, nowMs);

            // 2) LapData
            else if (pid == 2 && parsed.LapData != null)
                IngestLapData(sid, sess, parsed.LapData, nowMs);

            // 7) CarStatus (per-car assists + ERS telemetry)
            else if (pid == 7 && parsed.CarStatus != null)
                IngestCarStatus(sid, parsed.CarStatus, nowMs);

            // 10) CarDamage
            else if (pid == 10 && parsed.CarDamage != null)
                IngestCarDamage(sid, parsed.CarDamage);
        }

        private void IngestSession(SessionRun sess, SessionData s, long nowMs)
        {
            // v1.1.41/v1.1.47: are the DEEP Session fields (VSC/red-flag counts, lobby
            // settings/assists at payload offset ~639+) reliably mapped for this wire
            // format? Now 2025 AND 2026 — the 2026 offsets were verified byte-for-byte
            // against the official EA 2026 UDP spec (see GameInfo.AreDeepSessionFieldsMapped).
            // Early Session packets arrive zeroed, so the hasData/LobbySettingsCaptured
            // latch below only commits once non-zero settings appear.
            bool deepMapped = Packets.GameInfo.AreDeepSessionFieldsMapped(sess.LastPacketFormat);

            sess.SessionType = s.SessionType;
            sess.TrackId = s.TrackId;
            if (s.TotalLaps > 0) sess.TotalLaps = s.TotalLaps;
            if (s.TrackLength > 0) sess.TrackLength = s.TrackLength;
            sess.SessionTimeLeftSec = s.SessionTimeLeft;

            // Detect lobby changes (track change = different lobby)
            if (s.TrackId >= 0)
                CheckLobbyChange(s.TrackId);
            sess.Weather = s.Weather;
            sess.SafetyCarStatus = s.SafetyCarStatus;

            if (deepMapped)
            {
                if (s.NumVirtualSafetyCarPeriods > sess.NumVSCDeployments)
                    sess.NumVSCDeployments = s.NumVirtualSafetyCarPeriods;
                if (s.NumRedFlagPeriods > sess.NumRedFlagPeriods)
                    sess.NumRedFlagPeriods = s.NumRedFlagPeriods;
            }
            sess.NetworkGame = s.NetworkGame;

            sess.LatestTrackTempC = s.TrackTempC;
            sess.LatestAirTempC = s.AirTempC;
            sess.IsSpectating = s.IsSpectating;
            sess.GamePaused = s.GamePaused;
            sess.SpectatorCarIndex = s.SpectatorCarIndex;

            if (s.Weather != sess.LastWeatherState)
            {
                sess.WeatherTimeline.Add(new WeatherTimelineEntry
                {
                    TsMs = nowMs,
                    Weather = s.Weather,
                    TrackTempC = s.TrackTempC,
                    AirTempC = s.AirTempC,
                });
                sess.LastWeatherState = s.Weather;
            }

            // Weather forecast (overwrite each tick — last snapshot wins)
            if (s.WeatherForecast != null && s.WeatherForecast.Length > 0)
            {
                var fc = new List<Dictionary<string, object>>();
                foreach (var f in s.WeatherForecast)
                {
                    fc.Add(new Dictionary<string, object>
                    {
                        { "timeOffsetMin", (int)f.TimeOffsetMin },
                        { "weather", (int)f.Weather },
                        { "trackTempC", (int)f.TrackTempC },
                        { "airTempC", (int)f.AirTempC },
                        { "rainPercentage", (int)f.RainPercentage },
                    });
                }
                sess.WeatherForecast = fc;
            }

            // Lobby settings — keep updating until we see non-zero data.
            // Early Session packets may arrive before the game populates lobby fields.
            //
            // Deep lobby settings/assists: only when this wire format has them
            // reliably mapped (see deepMapped at the top). Now true for 2025 AND 2026
            // (2026 offsets verified vs the official spec). For any future unmapped
            // format, LobbySettingsCaptured stays false and the finalizer omits the
            // lobbySettings block instead of emitting coincidental garbage.
            bool hasData = deepMapped && (s.CarDamage != 0 || s.Collisions != 0 || s.RuleSet != 0
                || s.SafetyCarSetting != 0 || s.RedFlagsSetting != 0
                || s.SteeringAssist != 0 || s.GearboxAssist != 0
                || s.FormationLap != 0 || s.EqualCarPerformance != 0);

            if (deepMapped && (!sess.LobbySettingsCaptured || hasData))
            {
                sess.ForecastAccuracy = s.ForecastAccuracy;
                sess.SteeringAssist = s.SteeringAssist;
                sess.BrakingAssist = s.BrakingAssist;
                sess.GearboxAssist = s.GearboxAssist;
                sess.PitAssist = s.PitAssist;
                sess.PitReleaseAssist = s.PitReleaseAssist;
                sess.ERSAssist = s.ERSAssist;
                sess.DRSAssist = s.DRSAssist;
                sess.DynamicRacingLine = s.DynamicRacingLine;
                sess.DynamicRacingLineType = s.DynamicRacingLineType;
                sess.RuleSet = s.RuleSet;
                sess.RaceStarts = s.RaceStarts;
                sess.RecoveryMode = s.RecoveryMode;
                sess.FlashbackLimit = s.FlashbackLimit;
                sess.EqualCarPerformance = s.EqualCarPerformance;
                sess.SurfaceType = s.SurfaceType;
                sess.LowFuelMode = s.LowFuelMode;
                sess.TyreTemperature = s.TyreTemperature;
                sess.PitLaneTyreSim = s.PitLaneTyreSim;
                sess.CarDamage = s.CarDamage;
                sess.CarDamageRate = s.CarDamageRate;
                sess.Collisions = s.Collisions;
                sess.CollisionsOffForFirstLapOnly = s.CollisionsOffForFirstLapOnly;
                sess.CornerCuttingStringency = s.CornerCuttingStringency;
                sess.ParcFermeRules = s.ParcFermeRules;
                sess.FormationLap = s.FormationLap;
                sess.SafetyCarSetting = s.SafetyCarSetting;
                sess.RedFlagsSetting = s.RedFlagsSetting;
                if (hasData) sess.LobbySettingsCaptured = true;
            }
        }

        private const float CarStatusFuelCapacityMinKg = 5f;

        // v1.1.34 — ERS regulation capacity: 4 MJ = 4 000 000 J = 100%.
        private const float ErsMaxJoules = 4000000f;
        // Detection epsilon for lap rollover (counter dropping sharply).
        // A real volta-end drop is from ~95-100% to ~0%, way larger than any
        // network jitter (~0.1% between consecutive samples).
        private const float ErsRolloverDropPct = 5f;

        private void IngestCarStatus(string sid, CarStatusEntry[] entries, long nowMs)
        {
            if (entries == null) return;
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i] == null) continue;
                var d = EnsureDriver(sid, i);
                if (d == null) continue;

                byte newTc = entries[i].TractionControl;
                byte newAbs = entries[i].AntiLockBrakes;

                if (!d.AssistsCaptured)
                {
                    d.TractionControl = newTc;
                    d.AntiLockBrakes = newAbs;
                    d.AssistsCaptured = true;
                }
                else
                {
                    // Prefer lowest (most restrictive) value: post-race packets
                    // send defaults (TC=2/Full, ABS=1/On) that must not overwrite
                    // the correct mid-race values.
                    if (newTc < d.TractionControl) d.TractionControl = newTc;
                    if (newAbs < d.AntiLockBrakes) d.AntiLockBrakes = newAbs;
                }

                // Live race UI: tyre compound + age (latest wins).
                if (entries[i].VisualTyreCompound != 0) d.VisualTyreCompound = entries[i].VisualTyreCompound;
                if (entries[i].ActualTyreCompound != 0) d.ActualTyreCompound = entries[i].ActualTyreCompound;
                d.TyresAgeLaps = entries[i].TyresAgeLaps;

                float cap = entries[i].FuelCapacity;
                if (cap >= CarStatusFuelCapacityMinKg)
                {
                    if (!d.FuelFirstSampleSet)
                    {
                        d.FuelInTankFirst = entries[i].FuelInTank;
                        d.FuelRemainingLapsFirst = entries[i].FuelRemainingLaps;
                        d.FuelFirstSampleSet = true;
                    }
                    d.FuelInTankLast = entries[i].FuelInTank;
                    d.FuelRemainingLapsLast = entries[i].FuelRemainingLaps;
                    d.FuelCapacityKg = cap;
                    d.FuelMixLast = entries[i].FuelMix;
                    d.FuelCaptured = true;
                }

                // v1.1.34 — ERS / battery telemetry. Only ingest when the
                // packet actually carried the bytes (ErsCaptured set by parser
                // when entry size >= 55). Older F1 versions / shorter packets
                // are silently ignored, leaving ErsCaptured=false on driver.
                if (entries[i].ErsCaptured)
                    IngestErsForDriver(d, entries[i]);
            }
        }

        private static void IngestErsForDriver(DriverRun d, CarStatusEntry e)
        {
            float storePct = (e.ErsStoreEnergy / ErsMaxJoules) * 100f;
            if (storePct < 0f) storePct = 0f;
            if (storePct > 100f) storePct = 100f;

            float deployedPctNow = (e.ErsDeployedThisLap / ErsMaxJoules) * 100f;
            float hMgukPctNow = (e.ErsHarvestedThisLapMguk / ErsMaxJoules) * 100f;
            float hMguhPctNow = (e.ErsHarvestedThisLapMguh / ErsMaxJoules) * 100f;
            if (deployedPctNow < 0f) deployedPctNow = 0f;
            if (hMgukPctNow < 0f) hMgukPctNow = 0f;
            if (hMguhPctNow < 0f) hMguhPctNow = 0f;

            // Detect lap rollover via counter reset: the game zeroes
            // ErsDeployedThisLap at the start-line crossing. We assume any
            // drop greater than ErsRolloverDropPct indicates a new lap. The
            // *previous* snapshot is the end-of-lap value, so we push it to
            // the per-lap array before updating the snapshot.
            //
            // We only push when ErsFirstSampleSet is true, otherwise the very
            // first packet (which may carry leftover values from the previous
            // session) would produce a phantom lap[0] entry.
            if (d.ErsFirstSampleSet && deployedPctNow + ErsRolloverDropPct < d.DeployedPctLastSnapshot)
            {
                d.DeployedPctPerLap.Add(d.DeployedPctLastSnapshot);
                d.HarvestedMgukPctPerLap.Add(d.HarvestedMgukPctLastSnapshot);
                d.HarvestedMguhPctPerLap.Add(d.HarvestedMguhPctLastSnapshot);
                d.DeployedPctLastSnapshot = 0f;
                d.HarvestedMgukPctLastSnapshot = 0f;
                d.HarvestedMguhPctLastSnapshot = 0f;
            }

            // Per-lap counters are monotonically non-decreasing within a lap
            // (the game only adds to them). Track running maximum to be
            // resilient against any momentary fluctuation.
            if (deployedPctNow > d.DeployedPctLastSnapshot) d.DeployedPctLastSnapshot = deployedPctNow;
            if (hMgukPctNow > d.HarvestedMgukPctLastSnapshot) d.HarvestedMgukPctLastSnapshot = hMgukPctNow;
            if (hMguhPctNow > d.HarvestedMguhPctLastSnapshot) d.HarvestedMguhPctLastSnapshot = hMguhPctNow;

            bool paused = e.NetworkPaused != 0;

            if (paused)
            {
                // Pause samples do not contribute to the mean or to min/max
                // (the driver is frozen). They are counted so the consumer
                // can detect long disconnections that distorted the capture.
                d.ErsSamplesPaused++;
                d.ErsCaptured = true;
                return;
            }

            d.ErsSamplesCount++;
            d.ErsStorePctSumSimple += storePct;

            if (!d.ErsFirstSampleSet)
            {
                d.ErsStorePctFirst = storePct;
                d.ErsStorePctMin = storePct;
                d.ErsStorePctMax = storePct;
                d.ErsStorePctLast = storePct;
                d.ErsFirstSampleSet = true;
            }
            else
            {
                // ARITHMETIC mean — sampling at ~10Hz is uniform enough that
                // a time-weighted mean is statistically equivalent. An early
                // weighted-mean implementation surfaced two CI edge cases
                // (sub-millisecond dispatch collapsing most dtMs to 0 OR
                // a single non-zero dtMs dominating the weighted sum) with
                // no precision gain in production. Simple-mean is robust in
                // both environments; ErsStorePctSumSimple is updated above.
                if (storePct < d.ErsStorePctMin) d.ErsStorePctMin = storePct;
                if (storePct > d.ErsStorePctMax) d.ErsStorePctMax = storePct;
                d.ErsStorePctLast = storePct;
            }

            d.ErsDeployModeLast = e.ErsDeployMode;
            d.ErsCaptured = true;
        }

        private void IngestLobbyInfo(string sid, SessionRun sess, LobbyInfoData lobby)
        {
            if (lobby == null || lobby.Entries == null) return;
            DiagLobbyInfoReceived++;
            DiagLobbyInfoPlayers = lobby.NumPlayers;
            LobbyNumPlayers = lobby.NumPlayers;

            for (int i = 0; i < lobby.Entries.Length; i++)
            {
                var entry = lobby.Entries[i];
                if (entry == null) continue;
                if (entry.TeamId == 255) continue;

                string name = entry.Name;
                if (string.IsNullOrWhiteSpace(name)) continue;

                bool nameIsGeneric = IsGenericTag(name);
                string lookupKey = string.Format("{0}_{1}", entry.CarNumber, entry.TeamId);

                int nameReliability;
                if (entry.ShowOnlineNames != 0 && !entry.AiControlled)
                    nameReliability = TAG_RELIABLE;
                else
                    nameReliability = TAG_UNRELIABLE;

                string existing;
                if (nameIsGeneric && _lobbyNameByTeamRn.TryGetValue(lookupKey, out existing)
                    && !IsGenericTag(existing))
                {
                    // keep existing non-generic name
                }
                else
                {
                    _lobbyNameByTeamRn[lookupKey] = name;
                }

                int teamIdVal = entry.TeamId;
                HashSet<string> teamKeys;
                if (!_lobbyTeamKeys.TryGetValue(teamIdVal, out teamKeys))
                {
                    teamKeys = new HashSet<string>();
                    _lobbyTeamKeys[teamIdVal] = teamKeys;
                }
                teamKeys.Add(lookupKey);
                if (teamKeys.Count > 1)
                    _lobbyNameByTeamOnly[teamIdVal] = null;

                if (!nameIsGeneric)
                {
                    int existingBktRel;
                    if (!_bestKnownTagReliability.TryGetValue(lookupKey, out existingBktRel))
                        existingBktRel = TAG_GENERIC;
                    if (nameReliability >= existingBktRel)
                    {
                        _bestKnownTags[lookupKey] = name;
                        _bestKnownTagReliability[lookupKey] = nameReliability;
                    }

                    if (nameReliability >= TAG_RELIABLE && teamKeys.Count <= 1)
                    {
                        string existingTeamName;
                        if (!_lobbyNameByTeamOnly.TryGetValue(teamIdVal, out existingTeamName))
                        {
                            _lobbyNameByTeamOnly[teamIdVal] = name;
                        }
                        else if (existingTeamName != null && existingTeamName != name)
                        {
                            _lobbyNameByTeamOnly[teamIdVal] = null;
                        }
                    }
                }
            }
        }

        private static readonly System.Text.RegularExpressions.Regex _genericTagRe =
            new System.Text.RegularExpressions.Regex(
                @"^(Driver_\d+|Player_\d+|Player #\d+|Player|Car_\d+|Car\d+)$",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        private static bool IsGenericTag(string tag)
        {
            return !string.IsNullOrEmpty(tag) && _genericTagRe.IsMatch(tag);
        }

        private void IngestParticipants(string sid, SessionRun sess, ParticipantsData p, int playerCarIndex)
        {
            var entries = p.Entries;
            if (entries == null) return;

            DiagParticipantsReceived++;
            DiagParticipantsNumActive = p.NumActiveCars;
            DiagPlayerCarIdx = playerCarIndex;

            if (p.NumActiveCars > sess.MaxNumActiveCars)
                sess.MaxNumActiveCars = p.NumActiveCars;
            if (p.NumActiveCars > sess.ParticipantsPeakNumActive)
                sess.ParticipantsPeakNumActive = p.NumActiveCars;

            // Track human carIdx: once seen as human, stays human forever in this session.
            // Only check the active range (i < NumActiveCars) to prevent overflow
            // entries (AI grid fillers) from being falsely marked as human when the
            // game sends early packets with incorrect AiControlled=false flags.
            int humanScanLimit = Math.Min(p.NumActiveCars, entries.Length);
            for (int i = 0; i < humanScanLimit; i++)
            {
                if (entries[i] == null) continue;
                if (!entries[i].AiControlled && entries[i].Platform != 255)
                    sess.HumanCarIdxs[i] = true;
            }

            // Compute per-entry reliability for this packet
            var entryReliability = new Dictionary<int, int>();
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i] == null) continue;
                bool showOn = entries[i].ShowOnlineNames != 0;
                bool ai = entries[i].AiControlled;
                byte plat = entries[i].Platform;
                bool wasHuman;
                if (!sess.HumanCarIdxs.TryGetValue(i, out wasHuman)) wasHuman = false;

                if (showOn && !ai && plat != 255)
                    entryReliability[i] = TAG_RELIABLE;
                else if (wasHuman && !showOn)
                    entryReliability[i] = TAG_GENERIC;
                else if (ai && wasHuman)
                    entryReliability[i] = TAG_GENERIC;
                else if (ai && !wasHuman)
                    entryReliability[i] = TAG_UNRELIABLE;
                else
                    entryReliability[i] = TAG_UNRELIABLE;
            }

            // In offline mode (networkGame=0), showOnlineNames is irrelevant.
            if (sess.NetworkGame == 0)
            {
                for (int i = 0; i < p.NumActiveCars && i < entries.Length; i++)
                {
                    if (entries[i] != null
                        && entries[i].ShowOnlineNames == 0
                        && !entries[i].AiControlled
                        && !string.IsNullOrWhiteSpace(entries[i].Name)
                        && !IsGenericTag(entries[i].Name))
                    {
                        p.TagsByCarIdx[i] = entries[i].Name;
                        entryReliability[i] = TAG_RELIABLE;
                    }
                }
            }

            // ── Step 1: Store team data for ALL 22 entries ──
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i] == null) continue;
                if (entries[i].TeamId == 255 && entries[i].RaceNumber == 0) continue;

                ParticipantEntry existingTeam;
                if (sess.TeamByCarIdx.TryGetValue(i, out existingTeam) && existingTeam != null && existingTeam.TeamId != 255)
                {
                    if (entries[i].TeamId == 255)
                        continue;
                }
                sess.TeamByCarIdx[i] = entries[i];
            }

            if (DetectFullMyTeamGrid(sess, entries, p.NumActiveCars))
                _fullMyTeamStreak++;
            else
                _fullMyTeamStreak = 0;
            if (_fullMyTeamStreak >= 2)
                _captureFullMyTeam = true;

            // ── Step 1b: Detect raceNumber collisions among active network humans ──
            // Custom MyTeam online lobbies can assign the same m_raceNumber to two
            // different players (EA-acknowledged bug). Once we see two different
            // network humans with identical (raceNumber, teamId) in the same
            // packet, mark the rn-key ambiguous so subsequent fallback lookups
            // do not steal one player's name onto the other player's slot.
            {
                var seatHumans = new Dictionary<string, HashSet<byte>>();
                int activeScan = Math.Min(p.NumActiveCars, entries.Length);
                for (int i = 0; i < activeScan; i++)
                {
                    var e = entries[i];
                    if (e == null) continue;
                    if (e.TeamId == 255) continue;
                    if (e.AiControlled) continue;
                    if (e.NetworkId == 255) continue;
                    string rnKey = string.Format("{0}_{1}", e.RaceNumber, e.TeamId);
                    HashSet<byte> netIds;
                    if (!seatHumans.TryGetValue(rnKey, out netIds))
                    {
                        netIds = new HashSet<byte>();
                        seatHumans[rnKey] = netIds;
                    }
                    netIds.Add(e.NetworkId);
                }
                foreach (var kv in seatHumans)
                {
                    if (kv.Value.Count > 1 && _rnKeyAmbiguous.Add(kv.Key))
                    {
                        if (Notes.Count < 500)
                            Notes.Add(string.Format(
                                "rn-key ambiguous: {0} shared by {1} network humans (Custom MyTeam collision; using networkId)",
                                kv.Key, kv.Value.Count));
                    }
                }
            }

            // ── Step 2: Force Driver_X for unreliable names on known-human carIdx ──
            var tags = p.TagsByCarIdx;
            if (tags == null) tags = new Dictionary<int, string>();

            foreach (var kvp in new Dictionary<int, string>(tags))
            {
                int rel;
                if (!entryReliability.TryGetValue(kvp.Key, out rel)) rel = TAG_UNRELIABLE;
                if (rel == TAG_GENERIC && !IsGenericTag(kvp.Value))
                    tags[kvp.Key] = "Driver_" + kvp.Key;
            }

            // ── Step 3: Resolve generic names via lobby lookup ──
            foreach (var kvp in new Dictionary<int, string>(tags))
            {
                int carIdx = kvp.Key;
                string tag = kvp.Value;
                ParticipantEntry entry = (carIdx < entries.Length) ? entries[carIdx] : null;
                int rn = (entry != null) ? entry.RaceNumber : 0;
                int tid = (entry != null) ? entry.TeamId : -1;
                string lookupKey = string.Format("{0}_{1}", rn, tid);
                string netKey = ComputeNetKey(entry);

                int rel;
                if (!entryReliability.TryGetValue(carIdx, out rel)) rel = TAG_UNRELIABLE;

                if (!IsGenericTag(tag) && !string.IsNullOrWhiteSpace(tag))
                {
                    // Only store in bestKnownTags if source is reliable (not AI seat name)
                    if (rel >= TAG_RELIABLE)
                    {
                        // ── Write to network-id-keyed map (immune to rn collisions) ──
                        if (netKey != null)
                        {
                            int existingNetRel;
                            if (!_bestKnownTagReliabilityByNet.TryGetValue(netKey, out existingNetRel))
                                existingNetRel = TAG_GENERIC;
                            if (rel >= existingNetRel)
                            {
                                _bestKnownTagsByNet[netKey] = tag;
                                _bestKnownTagReliabilityByNet[netKey] = rel;
                            }
                        }

                        // ── Detect rn-key conflict and mark ambiguous ──
                        // If a different real name already lives on the same
                        // (raceNumber, teamId), the rn-keyed map is poisoned.
                        string existingRnName;
                        if (_bestKnownTags.TryGetValue(lookupKey, out existingRnName)
                            && !string.IsNullOrEmpty(existingRnName)
                            && !IsGenericTag(existingRnName)
                            && existingRnName != tag)
                        {
                            if (_rnKeyAmbiguous.Add(lookupKey) && Notes.Count < 500)
                                Notes.Add(string.Format(
                                    "rn-key conflict on write: {0} '{1}' vs '{2}' (using networkId)",
                                    lookupKey, existingRnName, tag));
                        }

                        if (_captureFullMyTeam)
                        {
                            string lobbyNm;
                            if (_lobbyNameByTeamRn.TryGetValue(lookupKey, out lobbyNm)
                                && !string.IsNullOrEmpty(lobbyNm) && !IsGenericTag(lobbyNm))
                            {
                                // Lobby already owns this seat key — do not overwrite with Participant.
                            }
                            else
                            {
                                _bestKnownTags[lookupKey] = tag;
                                _bestKnownTagReliability[lookupKey] = rel;
                            }
                        }
                        else
                        {
                            _bestKnownTags[lookupKey] = tag;
                            _bestKnownTagReliability[lookupKey] = rel;
                        }
                    }
                }
                else if (IsGenericTag(tag))
                {
                    string resolved = ResolveLobbyName(entry);
                    if (!string.IsNullOrEmpty(resolved))
                    {
                        // AI guard: do not let an AI-controlled slot inherit a
                        // name already assigned to a confirmed-human carIdx in
                        // this session. Prevents AI fillers from stealing real
                        // gamertags via shared (raceNumber, teamId).
                        bool isAi = entry != null && entry.AiControlled;
                        bool nameTakenByHuman = false;
                        if (isAi)
                        {
                            foreach (var existKvp in sess.TagsByCarIdx)
                            {
                                if (existKvp.Key == carIdx) continue;
                                if (existKvp.Value != resolved) continue;
                                bool wasHumanCar;
                                if (sess.HumanCarIdxs.TryGetValue(existKvp.Key, out wasHumanCar) && wasHumanCar)
                                {
                                    nameTakenByHuman = true;
                                    break;
                                }
                            }
                        }
                        if (!nameTakenByHuman)
                            tags[carIdx] = resolved;
                    }
                }
            }

            // Resolve player's gamer tag to real driver name via DriverId
            if (playerCarIndex >= 0 && playerCarIndex < 255 && tags.ContainsKey(playerCarIndex))
            {
                var playerEntry = (playerCarIndex < entries.Length) ? entries[playerCarIndex] : null;
                if (playerEntry != null && !playerEntry.AiControlled && playerEntry.DriverId != 255)
                {
                    string driverName;
                    if (Lookups.DriverById.TryGetValue(playerEntry.DriverId, out driverName))
                    {
                        string currentTag = tags[playerCarIndex];
                        bool alreadyKnown = false;
                        foreach (var kv in Lookups.DriverById)
                        {
                            if (string.Equals(kv.Value, currentTag, StringComparison.OrdinalIgnoreCase))
                            { alreadyKnown = true; break; }
                        }
                        if (!alreadyKnown)
                            tags[playerCarIndex] = driverName;
                    }
                }
            }

            // ── Step 4: Authoritative tag assignment with reliability-aware downgrade ──
            var existingTagToIdx = new Dictionary<string, int>();
            foreach (var kvp in sess.TagsByCarIdx)
                existingTagToIdx[kvp.Value] = kvp.Key;

            var safeTags = new Dictionary<int, string>();
            var safeReliability = new Dictionary<int, int>();
            foreach (var kvp in tags)
            {
                int carIdx = kvp.Key;
                string newTag = kvp.Value;
                if (string.IsNullOrWhiteSpace(newTag)) continue;

                int newRel;
                if (!entryReliability.TryGetValue(carIdx, out newRel)) newRel = TAG_UNRELIABLE;

                int existingIdx;
                if (existingTagToIdx.TryGetValue(newTag, out existingIdx) && existingIdx != carIdx)
                {
                    string oldIdxTagInPacket;
                    bool oldIdxInPacket = tags.TryGetValue(existingIdx, out oldIdxTagInPacket);
                    bool allowMove;
                    if (!oldIdxInPacket)
                        allowMove = true;
                    else if (oldIdxTagInPacket != newTag)
                        allowMove = true;
                    else
                        allowMove = false;

                    if (allowMove)
                    {
                        string oldTagAtOldIdx;
                        if (sess.TagsByCarIdx.TryGetValue(existingIdx, out oldTagAtOldIdx)
                            && oldTagAtOldIdx == newTag)
                        {
                            sess.TagsByCarIdx.Remove(existingIdx);
                            DriverRun dr;
                            if (sess.Drivers.TryGetValue(newTag, out dr))
                                dr.CarIdx = carIdx;
                        }
                        existingTagToIdx.Remove(newTag);
                    }
                    else
                    {
                        continue;
                    }
                }

                // Reliability-aware "never downgrade" logic
                if (IsGenericTag(newTag))
                {
                    string currentTag;
                    if (sess.TagsByCarIdx.TryGetValue(carIdx, out currentTag)
                        && !string.IsNullOrEmpty(currentTag) && !IsGenericTag(currentTag))
                    {
                        int currentRel;
                        if (!sess.TagReliability.TryGetValue(carIdx, out currentRel))
                            currentRel = TAG_GENERIC;
                        if (currentRel >= TAG_RELIABLE)
                            continue;
                        // Allow downgrade of UNRELIABLE AI name to Driver_X
                        // only when this carIdx is confirmed human with privacy on
                        bool wasHuman;
                        if (!sess.HumanCarIdxs.TryGetValue(carIdx, out wasHuman)) wasHuman = false;
                        var ent = (carIdx < entries.Length) ? entries[carIdx] : null;
                        bool showOn = (ent != null) ? ent.ShowOnlineNames != 0 : false;
                        if (wasHuman && !showOn)
                        { /* allow Driver_X to replace AI name */ }
                        else
                            continue;
                    }
                }

                bool dupInBatch = false;
                foreach (var other in safeTags)
                {
                    if (other.Value == newTag && other.Key != carIdx)
                    { dupInBatch = true; break; }
                }
                if (dupInBatch) continue;

                safeTags[carIdx] = newTag;
                safeReliability[carIdx] = newRel;
            }

            // Rename existing drivers if tag changed
            foreach (var kvp in safeTags)
            {
                int carIdx = kvp.Key;
                string newTag = kvp.Value;
                string oldTag;
                if (sess.TagsByCarIdx.TryGetValue(carIdx, out oldTag) && oldTag != null && oldTag != newTag)
                {
                    DriverRun dr;
                    if (sess.Drivers.TryGetValue(oldTag, out dr))
                    {
                        sess.Drivers.Remove(oldTag);
                        dr.Tag = newTag;
                        DriverRun existing;
                        if (sess.Drivers.TryGetValue(newTag, out existing))
                        {
                            if (dr.Laps.Count > existing.Laps.Count)
                                sess.Drivers[newTag] = dr;
                        }
                        else
                        {
                            sess.Drivers[newTag] = dr;
                        }
                    }
                }
            }

            foreach (var kvp in safeTags)
            {
                sess.TagsByCarIdx[kvp.Key] = kvp.Value;
                int rel;
                if (safeReliability.TryGetValue(kvp.Key, out rel))
                    sess.TagReliability[kvp.Key] = rel;
            }

            if (sess.TagsByCarIdx.Count > sess.MaxNumActiveCars)
                sess.MaxNumActiveCars = sess.TagsByCarIdx.Count;

            foreach (var kvp in safeTags)
            {
                if (string.IsNullOrEmpty(kvp.Value)) continue;
                EnsureDriver(sid, kvp.Key);
            }

            // ── Step 5: Resolve names for ALL entries that have team data but no/generic tag ──
            ResolveNamesFromLobby(sid, sess);
        }

        /// <summary>
        /// Build a network-id-based seat key. Returns null when the entry is not a
        /// network human or the networkId/teamId is the unknown sentinel (255).
        /// Used as primary disambiguator for raceNumber collisions (issue #1).
        /// </summary>
        private static string ComputeNetKey(ParticipantEntry e)
        {
            if (e == null) return null;
            if (e.AiControlled) return null;
            if (e.NetworkId == 255) return null;
            if (e.TeamId == 255) return null;
            return string.Format("net{0}_{1}", e.NetworkId, e.TeamId);
        }

        /// <summary>
        /// Public lookup used by LeagueFinalizer (RetroResolveNames) to apply the
        /// same priority order the store uses internally. Returns null when no
        /// confident name can be resolved (including when the rn-key is ambiguous).
        /// </summary>
        public string LookupBestKnownTagForEntry(ParticipantEntry entry)
        {
            return ResolveLobbyName(entry);
        }

        /// <summary>
        /// Strict version of <see cref="LookupBestKnownTagForEntry"/> — consults
        /// ONLY the unique-per-slot keys (network-id and raceNumber+team), and
        /// SKIPS the teamId-only fallback. Use this when the goal is to PROVE the
        /// slot belongs to a specific real player, not merely to pick a plausible
        /// name for it.
        ///
        /// Why: the teamId-only fallback (`_lobbyNameByTeamOnly[tid]`) was designed
        /// for a rare legacy case (Participants packet reports a different
        /// raceNumber than the lobby for an existing humano). It returns the
        /// "single human" of a team, but if F1 25 adds an AI grid filler to that
        /// same team, the AI slot inherits the human's name as positive evidence.
        /// That is exactly what let the Brazil_20260511 ci=19 (Visa Cash App #30)
        /// AI grid filler escape every phantom filter — `Drako%` was the only
        /// Visa Cash App human in the lobby, so `_lobbyNameByTeamOnly[6]` returned
        /// `Drako%` for the AI's slot, and `IsKnownRealPlayer` mistakenly took
        /// that as proof it was a real player.
        ///
        /// Callers that intend to RESOLVE a name to display (e.g.
        /// RetroResolveNames) should keep using the non-strict overload — they
        /// already know the slot is real and just need a label. Callers deciding
        /// whether to FILTER (IsPhantomEntry, ShouldSkipFcAiGridFillerRow,
        /// IsKnownRealPlayer) must use this strict version.
        /// </summary>
        public string LookupBestKnownTagForEntryStrict(ParticipantEntry entry)
        {
            if (entry == null) return null;
            int rn = entry.RaceNumber;
            int tid = entry.TeamId;
            string netKey = ComputeNetKey(entry);
            string rnKey = string.Format("{0}_{1}", rn, tid);

            string resolved;

            // 1) network-id key — unique per network player, no spoofing risk.
            if (netKey != null)
            {
                if (_bestKnownTagsByNet.TryGetValue(netKey, out resolved)
                    && !string.IsNullOrEmpty(resolved) && !IsGenericTag(resolved))
                    return resolved;
            }

            // 2) raceNumber key — unique per lobby seat, skip when ambiguous.
            if (!_rnKeyAmbiguous.Contains(rnKey))
            {
                if (_bestKnownTags.TryGetValue(rnKey, out resolved)
                    && !string.IsNullOrEmpty(resolved) && !IsGenericTag(resolved))
                    return resolved;
                if (_lobbyNameByTeamRn.TryGetValue(rnKey, out resolved)
                    && !string.IsNullOrEmpty(resolved) && !IsGenericTag(resolved))
                    return resolved;
            }

            // No teamId-only fallback by design.
            return null;
        }

        /// <summary>
        /// Entry-aware overload. Priority:
        ///   1. Network-id key (immune to raceNumber collisions in Custom MyTeam).
        ///   2. raceNumber key, only when NOT flagged ambiguous.
        ///   3. teamId-only fallback.
        /// </summary>
        private string ResolveLobbyName(ParticipantEntry entry)
        {
            if (entry == null) return null;
            int rn = entry.RaceNumber;
            int tid = entry.TeamId;
            string netKey = ComputeNetKey(entry);
            string rnKey = string.Format("{0}_{1}", rn, tid);

            string resolved;

            // 1) network-id key (intra-session unique per network player)
            if (netKey != null)
            {
                if (_bestKnownTagsByNet.TryGetValue(netKey, out resolved)
                    && !string.IsNullOrEmpty(resolved) && !IsGenericTag(resolved))
                    return resolved;
            }

            // 2) raceNumber key (cross-session fallback) — skip if ambiguous
            if (!_rnKeyAmbiguous.Contains(rnKey))
            {
                if (_bestKnownTags.TryGetValue(rnKey, out resolved)
                    && !string.IsNullOrEmpty(resolved) && !IsGenericTag(resolved))
                    return resolved;
                if (_lobbyNameByTeamRn.TryGetValue(rnKey, out resolved)
                    && !string.IsNullOrEmpty(resolved) && !IsGenericTag(resolved))
                    return resolved;
            }

            // 3) teamId-only fallback (single team in lobby)
            if (tid >= 0 && tid < 255)
            {
                string teamOnly;
                if (_lobbyNameByTeamOnly.TryGetValue(tid, out teamOnly)
                    && !string.IsNullOrEmpty(teamOnly) && !IsGenericTag(teamOnly))
                    return teamOnly;
            }

            return null;
        }

        /// <summary>
        /// Legacy (rn, teamId) overload — kept for FC bridge call sites that don't
        /// hold a full ParticipantEntry. Skips lookups when the rn-key has been
        /// flagged ambiguous (raceNumber collision detected via Participants).
        /// </summary>
        private string ResolveLobbyName(int raceNumber, int teamId)
        {
            string lookupKey = string.Format("{0}_{1}", raceNumber, teamId);
            string resolved = null;

            if (!_rnKeyAmbiguous.Contains(lookupKey))
            {
                if (_bestKnownTags.ContainsKey(lookupKey))
                    resolved = _bestKnownTags[lookupKey];
                else if (_lobbyNameByTeamRn.ContainsKey(lookupKey))
                    resolved = _lobbyNameByTeamRn[lookupKey];
            }

            // Fallback: teamId-only when primary key misses
            if ((string.IsNullOrEmpty(resolved) || IsGenericTag(resolved)) && teamId >= 0 && teamId < 255)
            {
                string teamOnly;
                if (_lobbyNameByTeamOnly.TryGetValue(teamId, out teamOnly) && teamOnly != null)
                    resolved = teamOnly;
            }

            if (IsGenericTag(resolved))
                return null;
            return resolved;
        }

        private void ResolveNamesFromLobby(string sid, SessionRun sess)
        {
            var tagToIdx = new Dictionary<string, int>();
            foreach (var kvp in sess.TagsByCarIdx)
                tagToIdx[kvp.Value] = kvp.Key;

            foreach (var kvp in new Dictionary<int, ParticipantEntry>(sess.TeamByCarIdx))
            {
                int carIdx = kvp.Key;
                var team = kvp.Value;
                if (team == null || team.TeamId == 255) continue;

                // Try to resolve via lobby/bestKnown maps first — this is positive
                // evidence that the slot is a real player. If we get a real name,
                // never skip the slot regardless of AiControlled or overflow status.
                string resolved = ResolveLobbyName(team);
                bool hasKnownName = !string.IsNullOrEmpty(resolved) && !IsGenericTag(resolved);

                bool wasHuman;
                bool confirmedHuman = sess.HumanCarIdxs.TryGetValue(carIdx, out wasHuman) && wasHuman;
                bool overflow = sess.NetworkGame == 1
                    && sess.ParticipantsPeakNumActive > 0
                    && carIdx >= sess.ParticipantsPeakNumActive;

                // Online: skip AI filler slots that are NOT confirmed real players.
                // Real players (with lobby/bestKnown name or confirmed human) are
                // preserved even if the AI flag is set (e.g. host migration, late
                // connect/disconnect cycles, slot-reassignment artifacts).
                if (sess.NetworkGame == 1
                    && team.AiControlled
                    && !hasKnownName
                    && !confirmedHuman)
                    continue;

                string existingTag;
                sess.TagsByCarIdx.TryGetValue(carIdx, out existingTag);

                if (!string.IsNullOrEmpty(existingTag) && !IsGenericTag(existingTag))
                    continue;

                if (string.IsNullOrEmpty(resolved))
                {
                    DiagLobbyFailed++;
                    // Still register with placeholder if no tag at all — ensures
                    // LapData/SessionHistory capture starts immediately.
                    // Skip overflow slots in online sessions when there is no
                    // positive evidence (lobby/bestKnown/wasHuman) — they are
                    // grid fillers even when AiControlled is stale/false.
                    if (string.IsNullOrEmpty(existingTag))
                    {
                        if (overflow && !confirmedHuman && !hasKnownName)
                            continue;

                        string placeholder = "Driver_" + carIdx;
                        if (!tagToIdx.ContainsKey(placeholder))
                        {
                            sess.TagsByCarIdx[carIdx] = placeholder;
                            tagToIdx[placeholder] = carIdx;
                            EnsureDriver(sid, carIdx);
                        }
                    }
                    continue;
                }

                int existingIdx;
                if (tagToIdx.TryGetValue(resolved, out existingIdx) && existingIdx != carIdx)
                    continue;

                DiagLobbyResolved++;

                if (!string.IsNullOrEmpty(existingTag) && existingTag != resolved)
                {
                    DriverRun dr;
                    if (sess.Drivers.TryGetValue(existingTag, out dr))
                    {
                        sess.Drivers.Remove(existingTag);
                        dr.Tag = resolved;
                        if (!sess.Drivers.ContainsKey(resolved))
                            sess.Drivers[resolved] = dr;
                    }
                    tagToIdx.Remove(existingTag);
                }

                sess.TagsByCarIdx[carIdx] = resolved;
                tagToIdx[resolved] = carIdx;
                EnsureDriver(sid, carIdx);
            }
        }

        private void IngestEvent(string sid, SessionRun sess, EventData evt, long nowMs)
        {
            string code = evt.Code;
            if (string.IsNullOrEmpty(code) || code.Length != 4) return;
            if (IgnoreEvents.Contains(code)) return;
            if (sess.Events.Count >= MaxEventsPerSession) return;

            var eventPayload = new Dictionary<string, object>
            {
                { "type", "EVENT" },
                { "code", code },
                { "tsMs", nowMs },
            };

            var data = new Dictionary<string, object>();
            if (code == "OVTK")
            {
                data["overtakerIdx"] = (int)evt.OvertakerIdx;
                data["overtakenIdx"] = (int)evt.OvertakenIdx;
            }
            else if (code == "PENA")
            {
                data["vehicleIdx"] = (int)evt.VehicleIdx;
                data["penaltyType"] = (int)evt.PenaltyType;
                data["infringementType"] = (int)evt.InfringementType;
                data["otherVehicleIdx"] = (int)evt.OtherVehicleIdx;
                data["timeSec"] = (int)evt.TimeSec;
                data["lapNum"] = (int)evt.LapNum;
                data["placesGained"] = (int)evt.PlacesGained;
            }
            else if (code == "DTSV" || code == "SGSV")
            {
                data["vehicleIdx"] = (int)evt.ServedVehicleIdx;
            }
            else if (code == "COLL")
            {
                data["vehicle1Idx"] = (int)evt.Vehicle1Idx;
                data["vehicle2Idx"] = (int)evt.Vehicle2Idx;
            }
            else if (code == "RTMT")
            {
                data["vehicleIdx"] = (int)evt.RetiredVehicleIdx;
                data["reason"] = (int)evt.RetiredReason;
            }
            else if (code == "FTLP")
            {
                data["vehicleIdx"] = (int)evt.FastestLapVehicleIdx;
                data["lapTimeSec"] = evt.FastestLapTimeSec;
            }
            else if (code == "SCAR")
            {
                data["safetyCarType"] = (int)evt.SafetyCarType;
                data["eventType"] = (int)evt.SafetyCarEventType;
            }
            if (data.Count > 0) eventPayload["data"] = data;

            sess.Events.Add(eventPayload);

            if (code == "SEND")
            {
                sess.SessionEndedAtMs = nowMs;
            }
            else if (code == "SSTA")
            {
                bool isOnline = sess.NetworkGame == 1;
                bool hasData = sess.Drivers.Count > 0 || sess.Events.Count > 1;
                if (hasData && !isOnline)
                {
                    foreach (var dr in sess.Drivers.Values)
                        dr.Reset();
                    sess.Events.Clear();
                    sess.Events.Add(eventPayload);
                    sess.FinalClassification = null;
                    sess.SessionEndedAtMs = null;
                    sess.NumSafetyCarDeployments = 0;
                    sess.NumVSCDeployments = 0;
                }
            }
            else if (code == "SCAR")
            {
                if (evt.SafetyCarType == 1 && evt.SafetyCarEventType == 0)
                    sess.NumSafetyCarDeployments++;
            }
            else if (code == "PENA")
            {
                var d = EnsureDriver(sid, evt.VehicleIdx);
                if (d != null && d.PenaltySnapshots.Count < 100)
                {
                    d.PenaltySnapshots.Add(new PenaltySnapshot
                    {
                        TsMs = nowMs,
                        PenaltyType = evt.PenaltyType,
                        InfringementType = evt.InfringementType,
                        OtherVehicleIdx = evt.OtherVehicleIdx,
                        TimeSec = evt.TimeSec,
                        LapNum = evt.LapNum,
                        PlacesGained = evt.PlacesGained,
                    });
                }
            }
            else if (code == "DTSV" || code == "SGSV")
            {
                var d = EnsureDriver(sid, evt.ServedVehicleIdx);
                if (d != null && d.PenaltySnapshots.Count < 100)
                {
                    d.PenaltySnapshots.Add(new PenaltySnapshot
                    {
                        TsMs = nowMs,
                        EventCode = code,
                    });
                }
            }
            else if (code == "COLL")
            {
                int v1 = evt.Vehicle1Idx;
                int v2 = evt.Vehicle2Idx;
                var d1 = EnsureDriver(sid, v1);
                if (d1 != null && d1.PenaltySnapshots.Count < 100)
                {
                    d1.PenaltySnapshots.Add(new PenaltySnapshot
                    {
                        TsMs = nowMs,
                        EventCode = "COLL",
                        OtherVehicleIdx = v2,
                    });
                }
                var d2 = EnsureDriver(sid, v2);
                if (d2 != null && d2.PenaltySnapshots.Count < 100)
                {
                    d2.PenaltySnapshots.Add(new PenaltySnapshot
                    {
                        TsMs = nowMs,
                        EventCode = "COLL",
                        OtherVehicleIdx = v1,
                    });
                }
            }
        }

        private void IngestSessionHistory(string sid, SessionRun sess, SessionHistoryData sh, long nowMs)
        {
            DiagShReceived++;
            int carIdx = sh.CarIdx;
            var d = EnsureDriver(sid, carIdx);
            if (d == null)
            {
                if (sh.NumLaps > 0)
                {
                    d = EarlyRegisterDriver(sid, sess, carIdx);
                    if (d != null) DiagShEarlyRegister++;
                }
                if (d == null) { DiagShNoDriver++; return; }
            }

            var laps = sh.Laps;
            DiagShLapsParsed += (laps != null) ? laps.Length : 0;

            // Dedup hash: numLaps + first 60 lap times
            int hashInput = sh.NumLaps;
            if (laps != null)
            {
                int count = Math.Min(laps.Length, 60);
                for (int i = 0; i < count; i++)
                    hashInput = hashInput * 31 + (int)laps[i].LapTimeMs;
            }
            if (d.LastHistoryHash.HasValue && d.LastHistoryHash.Value == hashInput
                && (nowMs - d.LastHistoryUpdateMs) < 1200)
            {
                DiagShDedup++;
                return;
            }

            var newLaps = new List<LapRecord>();
            if (laps != null)
            {
                for (int i = 0; i < laps.Length; i++)
                {
                    int lapTimeMs = (int)laps[i].LapTimeMs;
                    int lapNumber = laps[i].LapNumber;
                    if (lapTimeMs <= 0 || lapNumber <= 0)
                    {
                        DiagShLapsFiltered++;
                        continue;
                    }
                    if (lapTimeMs < 5000 || lapTimeMs > 600000)
                    {
                        DiagShLapsFiltered++;
                        continue;
                    }
                    DiagShLapsAccepted++;
                    newLaps.Add(new LapRecord
                    {
                        LapNumber = lapNumber,
                        LapTimeMs = lapTimeMs,
                        LapTime = MsToStr(lapTimeMs),
                        Sector1Ms = laps[i].Sector1Ms,
                        Sector2Ms = laps[i].Sector2Ms,
                        Sector3Ms = laps[i].Sector3Ms,
                        ValidFlags = laps[i].ValidFlags,
                        TsMs = nowMs,
                    });
                }
            }

            if (newLaps.Count > 0)
            {
                // Merge: keep existing laps not in new SH, update/add from new SH.
                // In spectator mode, the game may send partial SH (only the latest
                // lap) for non-spectated cars. A full replace would erase all
                // previously accumulated laps. Merging preserves them.
                var merged = new Dictionary<int, LapRecord>();
                foreach (var lap in d.Laps)
                    merged[lap.LapNumber] = lap;
                foreach (var lap in newLaps)
                    merged[lap.LapNumber] = lap;
                d.Laps = merged.Values.OrderBy(l => l.LapNumber).ToList();
                int maxLap = 0;
                for (int i = 0; i < d.Laps.Count; i++)
                    if (d.Laps[i].LapNumber > maxLap) maxLap = d.Laps[i].LapNumber;
                d.LastRecordedLapNumber = maxLap;
                d.LastCurrentLapNum = maxLap + 1;
            }

            // Tyre stints from SessionHistory
            if (sh.TyreStints != null)
            {
                d.TyreStints.Clear();
                for (int i = 0; i < sh.TyreStints.Length; i++)
                {
                    var ts = sh.TyreStints[i];
                    d.TyreStints.Add(new Dictionary<string, object>
                    {
                        { "stintIndex", ts.StintIndex },
                        { "endLap", (int)ts.EndLap },
                        { "tyreActual", (int)ts.TyreActual },
                        { "tyreVisual", (int)ts.TyreVisual },
                    });
                }
            }

            // Best times
            if (sh.Best != null)
            {
                d.Best = new Dictionary<string, object>
                {
                    { "bestLapTimeLapNum", (int)sh.Best.BestLapTimeLapNum },
                    { "bestLapTimeMs", sh.Best.BestLapTimeMs > 0 ? (object)(int)sh.Best.BestLapTimeMs : null },
                    { "bestSector1LapNum", (int)sh.Best.BestSector1LapNum },
                    { "bestSector1Ms", sh.Best.BestSector1Ms > 0 ? (object)sh.Best.BestSector1Ms : null },
                    { "bestSector2LapNum", (int)sh.Best.BestSector2LapNum },
                    { "bestSector2Ms", sh.Best.BestSector2Ms > 0 ? (object)sh.Best.BestSector2Ms : null },
                    { "bestSector3LapNum", (int)sh.Best.BestSector3LapNum },
                    { "bestSector3Ms", sh.Best.BestSector3Ms > 0 ? (object)sh.Best.BestSector3Ms : null },
                };
            }

            d.LastHistoryHash = hashInput;
            d.LastHistoryUpdateMs = nowMs;
        }

        /// <summary>
        /// drivers{} key that holds lap telemetry for this grid slot (Python _best_driver_tag_for_car_idx).
        /// </summary>
        private static string BestDriverTagForCarIdx(SessionRun sess, int carIdx)
        {
            string bestK = null;
            int bestN = -1;
            foreach (var cand in new[]
                     {
                         "Driver_" + carIdx,
                         "Car_" + carIdx,
                         "Car" + carIdx
                     })
            {
                DriverRun dr;
                if (!sess.Drivers.TryGetValue(cand, out dr) || dr == null) continue;
                int n = dr.Laps != null ? dr.Laps.Count : 0;
                if (n > bestN)
                {
                    bestN = n;
                    bestK = cand;
                }
            }
            foreach (var kvp in sess.Drivers)
            {
                var dr = kvp.Value;
                if (dr == null || dr.CarIdx != carIdx) continue;
                int n = dr.Laps != null ? dr.Laps.Count : 0;
                if (n > bestN)
                {
                    bestN = n;
                    bestK = kvp.Key;
                }
            }
            return bestN > 0 && !string.IsNullOrEmpty(bestK) ? bestK : null;
        }

        private static void MergeFcDriverBucket(SessionRun sess, int carIdx, string newTag, string oldTag)
        {
            if (string.IsNullOrEmpty(newTag)) return;
            if (string.IsNullOrEmpty(oldTag) || oldTag == newTag) return;
            DriverRun drOld;
            if (!sess.Drivers.TryGetValue(oldTag, out drOld) || drOld == null) return;
            sess.Drivers.Remove(oldTag);
            drOld.Tag = newTag;
            drOld.CarIdx = carIdx;
            DriverRun existing;
            if (sess.Drivers.TryGetValue(newTag, out existing) && existing != null)
            {
                int nOld = drOld.Laps != null ? drOld.Laps.Count : 0;
                int nEx = existing.Laps != null ? existing.Laps.Count : 0;
                if (nOld > nEx)
                    sess.Drivers[newTag] = drOld;
            }
            else
                sess.Drivers[newTag] = drOld;
        }

        private void IngestFinalClassification(SessionRun sess, FinalClassificationData fc, long nowMs)
        {
            // Accumulate FC: merge entries across repeated broadcasts.
            // The game may send FC packets with varying numCars in spectator mode.
            // Keep entries with Position > 0 from ANY packet so no classified car is lost.
            if (sess.FinalClassification == null)
            {
                sess.FinalClassification = fc;
            }
            else
            {
                var existing = sess.FinalClassification;
                for (int i = 0; i < fc.Classification.Length && i < existing.Classification.Length; i++)
                {
                    var newRow = fc.Classification[i];
                    if (newRow == null) continue;
                    var oldRow = existing.Classification[i];
                    if (newRow.Position > 0 && (oldRow == null || oldRow.Position <= 0))
                        existing.Classification[i] = newRow;
                    else if (newRow.Position > 0)
                        existing.Classification[i] = newRow;
                }
                if (fc.NumCars > existing.NumCars)
                    existing.NumCars = fc.NumCars;
            }
            sess.SessionEndedAtMs = nowMs;
            if (sess.Events.Count < MaxEventsPerSession)
            {
                sess.Events.Add(new Dictionary<string, object>
                {
                    { "type", "FINAL_CLASSIFICATION" },
                    { "code", "FINAL_CLASSIFICATION" },
                    { "tsMs", nowMs },
                });
            }

            if (fc.Classification == null) return;
            string sid = null;
            foreach (var kvp in Sessions)
            {
                if (kvp.Value == sess) { sid = kvp.Key; break; }
            }
            if (sid == null) return;

            // Count actual classified entries (Position > 0) across the full 22-slot array.
            // In spectator mode fc.NumCars may undercount; the real count is more reliable.
            int classifiedCount = 0;
            for (int i = 0; i < fc.Classification.Length; i++)
            {
                if (fc.Classification[i] != null && fc.Classification[i].Position > 0)
                    classifiedCount++;
            }
            int effectiveNumCars = Math.Max(fc.NumCars, classifiedCount);
            if (effectiveNumCars > sess.MaxNumActiveCars)
                sess.MaxNumActiveCars = effectiveNumCars;

            // FC rows are indexed by car index (fixed array[22]). row.CarIdx = array
            // index = true carIdx, set by the parser. Only process entries with Position > 0.
            for (int fcRow = 0; fcRow < fc.Classification.Length; fcRow++)
            {
                var row = fc.Classification[fcRow];
                if (row == null || row.Position <= 0) continue;

                int carIdx = row.CarIdx;

                // Online: skip overflow slots with 0 laps that were never occupied by
                // a real driver. The game fills grid positions beyond the active
                // participant count with AI placeholder data in qualifying FCs.
                // CRITICAL: only skip when there is NO positive evidence of a real
                // player (lobby map, bestKnown, networkId-key, wasHuman). A human
                // who joins the lobby and disconnects before completing a lap
                // would be classified DNF — never filtered.
                if (sess.NetworkGame == 1
                    && sess.ParticipantsPeakNumActive > 0
                    && carIdx >= sess.ParticipantsPeakNumActive
                    && row.NumLaps == 0)
                {
                    bool wasHuman;
                    bool confirmedHuman = sess.HumanCarIdxs.TryGetValue(carIdx, out wasHuman) && wasHuman;
                    bool hasKnownName = false;
                    ParticipantEntry slot;
                    if (sess.TeamByCarIdx.TryGetValue(carIdx, out slot) && slot != null && slot.TeamId != 255)
                    {
                        string knownName = ResolveLobbyName(slot);
                        hasKnownName = !string.IsNullOrEmpty(knownName) && !IsGenericTag(knownName);
                    }
                    if (!confirmedHuman && !hasKnownName)
                        continue;
                }

                string tag;
                bool weakTag = !sess.TagsByCarIdx.TryGetValue(carIdx, out tag) || string.IsNullOrEmpty(tag) || IsGenericTag(tag);

                // Cross-session tag recovery (prefer non-generic tags) — also when slot only has Driver_X / Car_X.
                if (weakTag)
                {
                    // v2.0.2 — the recovery below matches by carIdx across sessions. That
                    // is INVALID between the parts of a 3-part qualifying: F1 26 remaps
                    // carIdx between Q1/Q2/Q3 (5/6/7), so the same carIdx is a different
                    // driver in another part. When THIS session is a qualy part, skip other
                    // qualy parts (leave Qualy↔Race bridging, which is carIdx-comparable
                    // enough and covered by tests, untouched).
                    bool sessIsQualyPart = sess.SessionType.HasValue
                        && sess.SessionType.Value >= 5 && sess.SessionType.Value <= 7;
                    string bestCross = null;
                    ParticipantEntry bestCrossTeam = null;
                    foreach (var otherKvp in Sessions)
                    {
                        if (otherKvp.Value == sess) continue;
                        if (sessIsQualyPart)
                        {
                            var ov = otherKvp.Value;
                            bool otherIsQualyPart = ov.SessionType.HasValue
                                && ov.SessionType.Value >= 5 && ov.SessionType.Value <= 7;
                            if (otherIsQualyPart) continue; // don't cross a qualy-part carIdx remap
                        }
                        string crossTag;
                        if (otherKvp.Value.TagsByCarIdx.TryGetValue(carIdx, out crossTag)
                            && !string.IsNullOrEmpty(crossTag))
                        {
                            if (!IsGenericTag(crossTag))
                            {
                                bestCross = crossTag;
                                ParticipantEntry ct;
                                if (otherKvp.Value.TeamByCarIdx.TryGetValue(carIdx, out ct))
                                    bestCrossTeam = ct;
                                break;
                            }
                            if (bestCross == null)
                            {
                                bestCross = crossTag;
                                ParticipantEntry ct;
                                if (otherKvp.Value.TeamByCarIdx.TryGetValue(carIdx, out ct))
                                    bestCrossTeam = ct;
                            }
                        }
                    }
                    if (!string.IsNullOrEmpty(bestCross))
                    {
                        sess.TagsByCarIdx[carIdx] = bestCross;
                        tag = bestCross;
                        if (bestCrossTeam != null && !sess.TeamByCarIdx.ContainsKey(carIdx))
                            sess.TeamByCarIdx[carIdx] = bestCrossTeam;
                    }
                }

                weakTag = !sess.TagsByCarIdx.TryGetValue(carIdx, out tag) || string.IsNullOrEmpty(tag) || IsGenericTag(tag);

                // FC validates that this car is real. Try lobby resolution before placeholder.
                if (weakTag)
                {
                    ParticipantEntry team;
                    if (sess.TeamByCarIdx.TryGetValue(carIdx, out team) && team != null && team.TeamId != 255)
                    {
                        string resolved = ResolveLobbyName(team);
                        if (!string.IsNullOrEmpty(resolved))
                        {
                            bool isDup = false;
                            foreach (var existKvp in sess.TagsByCarIdx)
                            {
                                if (existKvp.Key != carIdx && existKvp.Value == resolved)
                                { isDup = true; break; }
                            }
                            if (!isDup)
                            {
                                sess.TagsByCarIdx[carIdx] = resolved;
                                tag = resolved;
                            }
                        }
                    }
                }

                weakTag = !sess.TagsByCarIdx.TryGetValue(carIdx, out tag) || string.IsNullOrEmpty(tag) || IsGenericTag(tag);

                // Bridge FC slot to drivers{} bucket that has laps (Qualy→Race identity drift).
                if (weakTag)
                {
                    string bridged = BestDriverTagForCarIdx(sess, carIdx);
                    if (!string.IsNullOrEmpty(bridged))
                    {
                        string prevTag = null;
                        sess.TagsByCarIdx.TryGetValue(carIdx, out prevTag);
                        MergeFcDriverBucket(sess, carIdx, bridged, prevTag);
                        sess.TagsByCarIdx[carIdx] = bridged;
                        tag = bridged;
                    }
                }

                if (!sess.TagsByCarIdx.TryGetValue(carIdx, out tag) || string.IsNullOrEmpty(tag))
                {
                    bool online = sess.NetworkGame == 1;
                    tag = online ? ("Driver_" + carIdx) : ("Car_" + carIdx);
                    sess.TagsByCarIdx[carIdx] = tag;
                    DiagFcRegistered++;
                }
                else if (sess.NetworkGame == 1 && tag == "Car_" + carIdx)
                {
                    tag = "Driver_" + carIdx;
                    sess.TagsByCarIdx[carIdx] = tag;
                }

                DiagFcTotal++;
                var d = EnsureDriver(sid, carIdx);
                if (d == null) continue;

                // Tyre stints: FinalClassification is authoritative — use it when we have valid stint data
                if (row.NumTyreStints > 0 && row.TyreStintsActual != null && row.TyreStintsVisual != null && row.TyreStintsEndLaps != null)
                {
                    var stints = new List<Dictionary<string, object>>();
                    int numStints = Math.Min(row.NumTyreStints, (byte)8);
                    for (int i = 0; i < numStints; i++)
                    {
                        byte endLap = row.TyreStintsEndLaps[i];
                        byte tyreActual = row.TyreStintsActual[i];
                        byte tyreVisual = row.TyreStintsVisual[i];

                        if (endLap == 0 && tyreActual == 0 && tyreVisual == 0) continue;

                        stints.Add(new Dictionary<string, object>
                        {
                            { "stintIndex", i },
                            { "endLap", (int)endLap },
                            { "tyreActual", (int)tyreActual },
                            { "tyreVisual", (int)tyreVisual },
                        });
                    }

                    // FC is official; replace SessionHistory data when we have any valid stints
                    if (stints.Count > 0)
                        d.TyreStints = stints;
                }
            }

            // Register cars from TeamByCarIdx that still lack tags.
            // Skip overflow AI filler slots in online sessions only when there
            // is NO positive evidence of a real player (lobby/bestKnown/wasHuman).
            // v1.1.36 — cap bumped from 22 to MaxSupportedCars to cover F1 26 grids.
            for (int ci = 0; ci < GameInfo.MaxSupportedCars; ci++)
            {
                if (sess.TagsByCarIdx.ContainsKey(ci)) continue;
                ParticipantEntry team;
                if (!sess.TeamByCarIdx.TryGetValue(ci, out team)) continue;
                if (team == null || team.TeamId == 255) continue;

                string resolved = ResolveLobbyName(team);
                bool hasKnownName = !string.IsNullOrEmpty(resolved) && !IsGenericTag(resolved);

                if (sess.NetworkGame == 1
                    && sess.ParticipantsPeakNumActive > 0
                    && ci >= sess.ParticipantsPeakNumActive
                    && !hasKnownName)
                {
                    bool wasHuman;
                    if (!sess.HumanCarIdxs.TryGetValue(ci, out wasHuman) || !wasHuman)
                        continue;
                }

                if (!string.IsNullOrEmpty(resolved))
                {
                    bool isDup = false;
                    foreach (var kv in sess.TagsByCarIdx)
                    {
                        if (kv.Key != ci && kv.Value == resolved)
                        { isDup = true; break; }
                    }
                    if (!isDup)
                    {
                        sess.TagsByCarIdx[ci] = resolved;
                        EnsureDriver(sid, ci);
                        continue;
                    }
                }
                sess.TagsByCarIdx[ci] = "Driver_" + ci;
                EnsureDriver(sid, ci);
            }
        }

        // Track Map (live UI only). Updates world position for already-registered
        // drivers; EnsureDriver returns null for unmapped slots so no phantoms.
        private void IngestMotion(string sid, MotionEntry[] rows)
        {
            if (rows == null) return;
            for (int i = 0; i < rows.Length; i++)
            {
                if (rows[i] == null) continue;
                var d = EnsureDriver(sid, rows[i].CarIdx);
                if (d == null) continue;
                d.LiveWorldX = rows[i].WorldX;
                d.LiveWorldZ = rows[i].WorldZ;
                d.LiveYaw = rows[i].Yaw;
                d.LivePosValid = true;
            }
        }

        private void IngestCarTelemetry(string sid, CarTelemetryEntry[] rows)
        {
            if (rows == null) return;
            for (int i = 0; i < rows.Length; i++)
            {
                var r = rows[i];
                if (r == null) continue;
                var d = EnsureDriver(sid, r.CarIdx);
                if (d == null) continue;
                d.LiveTyreSurfFL = r.TyreSurfFL; d.LiveTyreSurfFR = r.TyreSurfFR;
                d.LiveTyreSurfRL = r.TyreSurfRL; d.LiveTyreSurfRR = r.TyreSurfRR;
                d.LiveTyreInnerFL = r.TyreInnerFL; d.LiveTyreInnerFR = r.TyreInnerFR;
                d.LiveTyreInnerRL = r.TyreInnerRL; d.LiveTyreInnerRR = r.TyreInnerRR;
                d.LiveBrakeFL = r.BrakeFL; d.LiveBrakeFR = r.BrakeFR;
                d.LiveBrakeRL = r.BrakeRL; d.LiveBrakeRR = r.BrakeRR;
                d.LiveEngineTemp = r.EngineTemp;
                d.LiveTelemValid = true;

                // Telemetry trace for the speed/throttle/brake/gear charts. On lap rollover
                // (lap number changed) the current trace becomes "previous". Sample by
                // ~25 m lap-distance bucket (lapDistance comes from LapData -> LiveLapDistanceM).
                int lapNum = d.LastCurrentLapNum ?? 0;
                if (lapNum != d.TraceLapNum)
                {
                    if (d.TraceLapNum >= 0 && d.TraceCur.Count > 0)
                        d.TracePrev = d.TraceCur;
                    d.TraceCur = new System.Collections.Generic.Dictionary<int, int[]>();
                    d.TraceLapNum = lapNum;
                }
                if (d.LiveLapDistanceM > 0f)
                {
                    int bucket = (int)(d.LiveLapDistanceM / 25f);
                    float thr = r.Throttle < 0f ? 0f : (r.Throttle > 1f ? 1f : r.Throttle);
                    float brk = r.Brake < 0f ? 0f : (r.Brake > 1f ? 1f : r.Brake);
                    // Traço v2: além de vel/acel/freio/marcha, grava ERS por posição — bateria %
                    // (0-100) e o modo de deploy (0=nenhum,1=médio,2=hotlap,3=overtake). Habilita o
                    // mapa de ERS por posição no portal do piloto. Campos extras são ignorados pelo
                    // renderizador ao vivo (usa só 1-4) e captados pelo backend (guarda o array cru).
                    d.TraceCur[bucket] = new int[] {
                        r.Speed, (int)Math.Round(thr * 100f), (int)Math.Round(brk * 100f), r.Gear,
                        (int)Math.Round(d.ErsStorePctLast), d.ErsDeployModeLast
                    };
                }
            }
        }

        private void IngestLapData(string sid, SessionRun sess, LapDataEntry[] rows, long nowMs)
        {
            for (int i = 0; i < rows.Length; i++)
            {
                var row = rows[i];
                // v1.1.36 — LapDataEntry.Parse now leaves trailing slots null
                // when the buffer holds fewer than NumCars (=MaxSupportedCars)
                // entries, which is the normal case on F1 25 (22 cars).
                if (row == null) continue;
                int carIdx = row.CarIdx;
                var d = EnsureDriver(sid, carIdx);
                if (d == null)
                {
                    // In spectator mode, active cars may have valid ResultStatus/DriverStatus
                    // even when CarPosition and CurrentLapNum are zeroed (outside camera view).
                    if (row.CarPosition > 0 || row.CurrentLapNum > 0
                        || row.ResultStatus > 1 || row.DriverStatus > 0)
                    {
                        d = EarlyRegisterDriver(sid, sess, carIdx);
                        if (d != null) DiagLdEarlyRegister++;
                    }
                    if (d == null) { DiagLdNoDriver++; continue; }
                }

                if (row.GridPosition > 0)
                    d.GridPosition = row.GridPosition;
                if (row.CarPosition > 0)
                    d.CarPosition = row.CarPosition;

                // Live race UI fields (latest LapData wins; not used by export).
                d.LiveDeltaToCarFrontMs = row.DeltaToCarFrontMs;
                d.LiveDeltaToLeaderMs = row.DeltaToLeaderMs;
                d.LiveCurrentLapTimeMs = (int)row.CurrentLapTimeInMS;
                d.LiveSector = row.Sector;
                d.LiveS1Ms = row.Sector1TimeInMS;
                d.LiveS2Ms = row.Sector2TimeInMS;
                d.LiveLapDistanceM = row.LapDistance;
                d.LivePitStatus = row.PitStatus;
                d.LivePenaltiesSec = row.Penalties;
                d.LiveResultStatus = row.ResultStatus;
                d.LiveDriverStatus = row.DriverStatus;
                d.LiveCurrentLapInvalid = row.CurrentLapInvalid;

                // Pit stops: track increments
                int numPit = row.NumPitStops;
                if (numPit >= 0 && numPit <= 10)
                {
                    if (!d.LastNumPitStops.HasValue)
                    {
                        d.LastNumPitStops = numPit;
                    }
                    else if (numPit > d.LastNumPitStops.Value)
                    {
                        if (d.PitStops.Count < MaxPitEventsPerDriver)
                        {
                            d.PitStops.Add(new PitStopRecord
                            {
                                NumPitStops = numPit,
                                TsMs = nowMs,
                                LapNum = row.CurrentLapNum > 0 ? (int?)row.CurrentLapNum : null,
                            });
                        }
                        d.LastNumPitStops = numPit;
                    }
                }

                int currentLap = row.CurrentLapNum;
                int lastLapTimeMs = (int)row.LastLapTimeInMS;
                int s1Ms = row.Sector1TimeInMS;
                int s2Ms = row.Sector2TimeInMS;

                if (lastLapTimeMs > 0 && lastLapTimeMs >= 5000 && lastLapTimeMs <= 600000)
                    d.LastSeenLapTimeMs = lastLapTimeMs;

                if (!d.LastCurrentLapNum.HasValue)
                {
                    DiagLdNoPrevLap++;
                }
                else if (currentLap > d.LastCurrentLapNum.Value)
                {
                    int completedLap = currentLap - 1;

                    // Tyre wear snapshot on lap completion
                    if (completedLap > 0 && d.LatestTyreWear != null)
                    {
                        bool twExists = false;
                        for (int j = 0; j < d.TyreWearPerLap.Count; j++)
                            if (d.TyreWearPerLap[j].LapNumber == completedLap) { twExists = true; break; }
                        if (!twExists)
                        {
                            var tw = d.LatestTyreWear;
                            d.TyreWearPerLap.Add(new TyreWearSnapshot
                            {
                                LapNumber = completedLap,
                                RL = tw.RL, RR = tw.RR, FL = tw.FL, FR = tw.FR,
                                Avg = (float)Math.Round((tw.RL + tw.RR + tw.FL + tw.FR) / 4.0, 1),
                            });
                        }
                    }

                    // Damage snapshot on lap completion
                    if (completedLap > 0 && d.LatestDamage != null)
                    {
                        bool dmgExists = false;
                        for (int j = 0; j < d.DamagePerLap.Count; j++)
                            if (d.DamagePerLap[j].LapNumber == completedLap) { dmgExists = true; break; }
                        if (!dmgExists)
                        {
                            var dmg = d.LatestDamage;
                            d.DamagePerLap.Add(new DamageSnapshot
                            {
                                LapNumber = completedLap,
                                WingFL = dmg.WingFrontLeft, WingFR = dmg.WingFrontRight, WingRear = dmg.WingRear,
                                TyreDmgRL = dmg.TyreDmgRL, TyreDmgRR = dmg.TyreDmgRR,
                                TyreDmgFL = dmg.TyreDmgFL, TyreDmgFR = dmg.TyreDmgFR,
                            });
                        }
                    }

                    // Record lap from LapData (fallback if SessionHistory didn't capture it)
                    if (lastLapTimeMs <= 0)
                    {
                        DiagLdTimeZero++;
                    }
                    else
                    {
                        int lapNumber = completedLap;
                        if (lapNumber <= d.LastRecordedLapNumber)
                        {
                            DiagLdAlreadyExists++;
                        }
                        else if (lastLapTimeMs < 5000 || lastLapTimeMs > 600000)
                        {
                            DiagLdSanityFail++;
                        }
                        else
                        {
                            bool exists = false;
                            for (int j = 0; j < d.Laps.Count; j++)
                                if (d.Laps[j].LapNumber == lapNumber) { exists = true; break; }
                            if (!exists)
                            {
                                int s3Ms = Math.Max(0, lastLapTimeMs - s1Ms - s2Ms);
                                d.Laps.Add(new LapRecord
                                {
                                    LapNumber = lapNumber,
                                    LapTimeMs = lastLapTimeMs,
                                    LapTime = MsToStr(lastLapTimeMs),
                                    Sector1Ms = s1Ms,
                                    Sector2Ms = s2Ms,
                                    Sector3Ms = s3Ms,
                                    ValidFlags = 0,
                                    TsMs = nowMs,
                                });
                                DiagLdLapRecorded++;
                                if (lapNumber > d.LastRecordedLapNumber)
                                    d.LastRecordedLapNumber = lapNumber;
                            }
                            else
                            {
                                DiagLdAlreadyExists++;
                            }
                        }
                    }
                }

                if (currentLap >= 0)
                    d.LastCurrentLapNum = currentLap;

                // Warnings tracking
                int totalWarn = row.TotalWarnings;
                int ccWarn = row.CornerCuttingWarnings;
                if (totalWarn > d.LastTotalWarnings || ccWarn > d.LastCornerCuttingWarnings)
                {
                    d.LastTotalWarnings = totalWarn;
                    d.LastCornerCuttingWarnings = ccWarn;
                }
            }
        }

        private void IngestCarDamage(string sid, CarDamageEntry[] rows)
        {
            for (int i = 0; i < rows.Length; i++)
            {
                var row = rows[i];
                // v1.1.36 — CarDamageEntry.Parse now leaves trailing slots null
                // when the buffer holds fewer than NumCars entries. Safe-skip.
                if (row == null) continue;
                int carIdx = row.CarIdx;
                var d = EnsureDriver(sid, carIdx);
                if (d == null) continue;

                d.LatestTyreWear = new TyreWearValues
                {
                    RL = row.TyreWear.RL,
                    RR = row.TyreWear.RR,
                    FL = row.TyreWear.FL,
                    FR = row.TyreWear.FR,
                };
                d.LatestDamage = new DamageValues
                {
                    WingFrontLeft = row.Wing.FrontLeft,
                    WingFrontRight = row.Wing.FrontRight,
                    WingRear = row.Wing.Rear,
                    TyreDmgRL = (int)row.TyresDamage.RL,
                    TyreDmgRR = (int)row.TyresDamage.RR,
                    TyreDmgFL = (int)row.TyresDamage.FL,
                    TyreDmgFR = (int)row.TyresDamage.FR,
                };
            }
        }
    }
}
