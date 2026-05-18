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
    }
}
