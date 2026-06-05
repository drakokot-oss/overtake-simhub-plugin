namespace Overtake.SimHub.Plugin.Packets
{
    /// <summary>
    /// Central constants and helpers for game-version awareness.
    ///
    /// v1.1.36 — Introduced to prepare the plugin for F1 26 (announced by
    /// Codemasters as a "mod" over F1 25, keeping the same UDP base). Key
    /// known changes for 2026: 11-team grid (Cadillac joins; Sauber rebrands
    /// to Audi), so the historical 22-car cap is no longer safe.
    ///
    /// We do NOT hard-code F1 26 team IDs / driver IDs here because the UDP
    /// spec was not public at the time this code shipped. The strategy is:
    ///   1. Lift the parser cap to a generous <see cref="MaxSupportedCars"/>
    ///      so larger grids are NOT silently truncated.
    ///   2. Parse defensively: each per-car loop bails out when the buffer
    ///      runs short. F1 25 packets (22 entries) are unaffected.
    ///   3. Detect the game version from <see cref="PacketHeader.PacketFormat"/>
    ///      and emit it dynamically in the JSON via <see cref="GameNameFromPacketFormat"/>.
    ///   4. When the F1 26 spec lands, only Lookups.Teams (add Cadillac / Audi)
    ///      and Lookups.DriverById (new rosters) need updating — the parser
    ///      structure should not.
    /// </summary>
    public static class GameInfo
    {
        /// <summary>
        /// Maximum number of car entries the parsers are willing to read from
        /// any per-car packet (Participants, LobbyInfo, LapData,
        /// FinalClassification, CarDamage, CarStatus).
        ///
        /// 26 = 11 teams * 2 (the announced F1 26 grid) + 4 wildcard/reserve
        /// slots. The cap is generous on purpose so a single Codemasters
        /// patch that adds a substitute or a 13th seat doesn't require a
        /// plugin update. Lifting it costs ~4 KB of array headroom per
        /// session (negligible).
        ///
        /// Per-packet parsers MUST also do `if (off + EntrySize > data.Length)
        /// break;` inside their loop so they degrade gracefully on smaller
        /// buffers (e.g. F1 25 sending exactly 22 entries).
        /// </summary>
        public const int MaxSupportedCars = 26;

        /// <summary>
        /// Translates the <see cref="PacketHeader.PacketFormat"/> u16 (always
        /// the first two bytes of every UDP packet) into the human-facing
        /// "game" string embedded in the exported JSON.
        ///
        /// Known mappings:
        ///   2025 -> "F1_25"
        ///   2026 -> "F1_26"  (educated guess — Codemasters historically bumps
        ///                     PacketFormat each year; subject to confirmation
        ///                     when the F1 26 spec/binary becomes available)
        /// Anything else falls back to "F1_{fmt}" so the field stays
        /// machine-readable and we can spot unexpected values in the wild.
        /// </summary>
        public static string GameNameFromPacketFormat(ushort fmt)
        {
            switch (fmt)
            {
                case 2025: return "F1_25";
                case 2026: return "F1_26";
                default:   return "F1_" + fmt.ToString();
            }
        }

        // ----------------------------------------------------------------------
        // UDP WIRE-FORMAT SUPPORT (Phase 2 readiness — added v1.1.39)
        // ----------------------------------------------------------------------
        //
        // The "UDP Format" option in the game menu (2023/2024/2025/2026) controls
        // the BYTE LAYOUT of each packet body, independently of which content is
        // loaded. The header (29 bytes, PacketFormat included) is stable across
        // formats, so we can always read PacketFormat — but the per-packet parsers
        // (ParticipantsData, CarStatusData, ...) use FIXED 2025 offsets.
        //
        // Empirical finding (v1.1.39, controlled capture): a capture taken with
        // "UDP Format = 2026" shifts the body layout of at least Participants
        // (packetId 4) and CarStatus (packetId 7) and adds a new per-frame packet
        // (packetId 16). The 2025-offset parsers then read garbage (corrupted
        // names, teamIds like 0/1/25/255, astronomical ERS values). The official
        // 2026 UDP spec deltas are not public yet, so we cannot ship 2026 offsets.
        //
        // STRATEGY:
        //   * 2025 format -> fully parsed (also carries 2026 *content*: team ids
        //     220-230, track 42 — those are content, not layout).
        //   * Unsupported format (2026+) -> the export is flagged loudly via
        //     _debug.game.unsupportedUdpFormat and _debug.rawSamples captures the
        //     raw packet bytes so the layout can be reverse-engineered offline.
        //
        // EXTENSION POINT (Phase 2 execution): when the 2026 layout is known, add
        // 2026 parser variants and route on PacketFormat. The cleanest seam is
        // PacketParser.Dispatch (it already reads the header); branch per format
        // there and call format-specific Parse overloads. Until then, this helper
        // is the single source of truth for "can we trust the parsed body?".

        /// <summary>
        /// The UDP wire formats whose packet body layout the parsers FULLY support.
        ///
        /// v1.1.41: 2026 is now SUPPORTED. Validated on a labeled human/online
        /// capture AND a full 22-car AI grid (every team mapped 1:1, ERS sane,
        /// LobbyInfo names recovered). Participants (4), CarStatus (7), LobbyInfo
        /// (9) have 2026 layouts; LapData/FinalClassification/CarDamage/Event and
        /// the Session core fields parse identically. The ONE field we cannot map
        /// reliably yet — the deep Session lobby settings/assists block — is
        /// OMITTED for 2026 (see AreDeepSessionFieldsMapped) instead of emitting
        /// coincidental garbage. Because 2026 is supported, the raw-sample
        /// collector and the unsupportedUdpFormat flag no longer fire for it.
        /// </summary>
        public static readonly int[] SupportedParseFormats = { 2025, 2026 };

        /// <summary>
        /// Whether the DEEP Session-packet fields (lobby settings / driver
        /// assists block at payload offset ~639+) are reliably mapped for the
        /// given wire format. The 2026 format shifts these by an amount we could
        /// not pin down from a first-occurrence sample (the early Session packet
        /// has them zeroed), so they are OMITTED for 2026 rather than guessed.
        /// Everything else in the Session packet (track, type, weather, temps,
        /// networkGame, safetyCarStatus, spectating, weather-forecast count) is in
        /// the unchanged early region and stays reliable.
        /// </summary>
        public static bool AreDeepSessionFieldsMapped(ushort fmt)
        {
            return fmt < 2026;
        }

        /// <summary>
        /// True when the per-packet parsers can be trusted for this PacketFormat.
        /// PacketFormat 0 means "header not observed yet" and is treated as
        /// supported (capture-less exports / tests). Any future format we have not
        /// implemented offsets for (e.g. 2026) returns false so the caller can
        /// flag the export instead of silently trusting garbage.
        /// </summary>
        public static bool IsParseSupportedFormat(ushort fmt)
        {
            if (fmt == 0) return true;
            for (int i = 0; i < SupportedParseFormats.Length; i++)
                if (SupportedParseFormats[i] == fmt) return true;
            return false;
        }

        /// <summary>
        /// True when a team id belongs to the F1 26 "2026 Season Pack" grid
        /// (220-230). Used to detect 2026 content even when the wire format is
        /// still 2025. Kept here (not in Finalizer.Lookups) so packet-layer code
        /// can use it without referencing the Finalizer namespace.
        /// </summary>
        public const int F1_26TeamIdMin = 220;
        public const int F1_26TeamIdMax = 230;
        public const int F1_26TrackIdMadring = 42;

        public static bool IsF1_26TeamId(int teamId)
        {
            return teamId >= F1_26TeamIdMin && teamId <= F1_26TeamIdMax;
        }
    }
}
