using System;
using System.Collections.Generic;
using System.Text;

namespace Overtake.SimHub.Plugin.Packets
{
    public class ParticipantEntry
    {
        public byte DriverId;
        /// <summary>
        /// F1 25 UDP packet 4, offset 2 inside each ParticipantData entry.
        /// "Unique identifier for network players" per F1 25 spec. Used as
        /// primary disambiguator for raceNumber collisions in Custom MyTeam
        /// online lobbies (issue #1). 255 = unknown / not applicable.
        /// </summary>
        public byte NetworkId;
        public byte TeamId;
        public bool MyTeam;
        public byte RaceNumber;
        public bool AiControlled;
        public byte Platform;
        public byte YourTelemetry;
        public byte ShowOnlineNames;
        public byte Nationality;
        public string Name;
    }

    /// <summary>
    /// Packet ID 4: Participants.
    /// F1 25 payload: 1 byte numActiveCars + 22 entries of 57 bytes each.
    /// F1 26 (mod over F1 25) is expected to carry up to 24 entries (Cadillac
    /// joins; Sauber rebrands to Audi). The parser reads up to
    /// <see cref="GameInfo.MaxSupportedCars"/> entries but bails out when the
    /// buffer runs short, so F1 25 packets (22) are unaffected.
    /// Name field: 32 bytes at offset 7 within each entry (null-terminated, UTF-8/Latin-1).
    ///
    /// ALL entries that fit in the buffer are parsed for team data (teamId,
    /// raceNumber, etc.) because this data is reliable even in overflow positions.
    /// TagsByCarIdx is populated only for active entries (0..numActive-1).
    /// For human players with showOnlineNames=0, the game sends the F1 seat name
    /// instead of the gamertag, so these are replaced with placeholders.
    /// Overflow/hidden names are resolved via LobbyInfo and cross-session recovery.
    /// </summary>
    public class ParticipantsData
    {
        private const int NameLen = 32;

        /// <summary>
        /// Byte layout of a single ParticipantData entry. Differs between the
        /// 2025 and 2026 UDP wire formats (the 2026 "Season Pack" inserts fields
        /// before teamId and grows the stride 57 -> 60). Offsets confirmed by
        /// reverse-engineering a labeled 2026 capture — see
        /// docs/F1-26-UDP-OFFSET-MAP.md. aiControlled (0), driverId (1) and
        /// networkId (2) are identical across formats.
        /// </summary>
        private struct Layout
        {
            public int Stride;
            public int OffTeamId, OffMyTeam, OffRaceNumber, OffNationality;
            public int OffName, OffYourTelemetry, OffShowOnlineNames, OffPlatform;
        }

        private static readonly Layout L2025 = new Layout
        {
            Stride = 57, OffTeamId = 3, OffMyTeam = 4, OffRaceNumber = 5, OffNationality = 6,
            OffName = 7, OffYourTelemetry = 39, OffShowOnlineNames = 40, OffPlatform = 43,
        };

        // v1.1.40 — F1 26 / 2026 Season Pack wire format. +2 bytes before teamId
        // (offsets 3,4 unknown=255) and +1 byte before myTeam (offset 6 unknown=1);
        // every later field shifts by +3 and the stride grows to 60.
        private static readonly Layout L2026 = new Layout
        {
            Stride = 60, OffTeamId = 5, OffMyTeam = 7, OffRaceNumber = 8, OffNationality = 9,
            OffName = 10, OffYourTelemetry = 42, OffShowOnlineNames = 43, OffPlatform = 46,
        };

        private static Layout LayoutForBodyWireFormat(ushort bodyWireFormat)
        {
            return bodyWireFormat >= 2026 ? L2026 : L2025;
        }

        /// <summary>
        /// Scores both 2025 and 2026 body layouts; used when the header says 2026.
        /// </summary>
        internal static ushort ProbeBodyWireFormat(byte[] data)
        {
            int score2025 = ScoreLayout(data, L2025);
            int score2026 = ScoreLayout(data, L2026);
            return score2025 > score2026 ? (ushort)2025 : (ushort)2026;
        }

        private static int ScoreLayout(byte[] data, Layout lay)
        {
            if (data == null || data.Length < PacketHeader.Size + 1)
                return int.MinValue / 4;

            int p = PacketHeader.Size;
            byte numActive = data[p];
            if (numActive == 0) return int.MinValue / 4;

            int baseOff = p + 1;
            int active = Math.Min((int)numActive, GameInfo.MaxSupportedCars);
            int score = 0;
            int myTeamCount = 0;
            int driverXCount = 0;

            for (int i = 0; i < active; i++)
            {
                int start = baseOff + i * lay.Stride;
                if (start + lay.Stride > data.Length)
                {
                    score -= 60;
                    break;
                }

                if (start + lay.OffMyTeam < data.Length && data[start + lay.OffMyTeam] != 0)
                    myTeamCount++;

                byte teamId = (start + lay.OffTeamId < data.Length) ? data[start + lay.OffTeamId] : (byte)255;
                if (teamId >= 220 && teamId <= 235) score += 4;
                else if (teamId == 255) score -= 10;

                string name = ParseName(data, start, i, lay);
                if (name.StartsWith("Driver_"))
                    driverXCount++;
                else
                    score += ScoreNameQuality(name);
            }

            score -= driverXCount * 45;
            // Full My Team grid with header 2026 => legacy 2025 body layout (Catalunya case).
            if (active >= 2 && myTeamCount == active) score += 120;
            // Official career grid: no MyTeam flags on active humans.
            else if (active >= 2 && myTeamCount == 0) score += 30;

            return score;
        }

        private static int ScoreNameQuality(string name)
        {
            if (string.IsNullOrEmpty(name)) return -25;
            int score = name.Length >= 4 ? 12 : 4;
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == ' ' || c == '[' || c == ']')
                    score += 2;
                else if (char.IsControl(c))
                    score -= 30;
                else
                    score -= 6;
            }
            return score;
        }

        public byte NumActiveCars;
        /// <summary>
        /// Slot-indexed array sized to <see cref="GameInfo.MaxSupportedCars"/>.
        /// Entries beyond what the buffer fits will be null.
        /// </summary>
        public ParticipantEntry[] Entries;
        /// <summary>Tags for active entries only (0..NumActiveCars-1).</summary>
        public Dictionary<int, string> TagsByCarIdx;

        private static ParticipantEntry ParseEntry(byte[] data, int start, int i, Layout lay)
        {
            return new ParticipantEntry
            {
                AiControlled = data[start + 0] != 0,
                DriverId = data[start + 1],
                NetworkId = (start + 2 < data.Length) ? data[start + 2] : (byte)255,
                TeamId = (start + lay.OffTeamId < data.Length) ? data[start + lay.OffTeamId] : (byte)255,
                MyTeam = (start + lay.OffMyTeam < data.Length) && data[start + lay.OffMyTeam] != 0,
                RaceNumber = (start + lay.OffRaceNumber < data.Length) ? data[start + lay.OffRaceNumber] : (byte)0,
                Nationality = (start + lay.OffNationality < data.Length) ? data[start + lay.OffNationality] : (byte)0,
                YourTelemetry = (start + lay.OffYourTelemetry < data.Length) ? data[start + lay.OffYourTelemetry] : (byte)0,
                ShowOnlineNames = (start + lay.OffShowOnlineNames < data.Length) ? data[start + lay.OffShowOnlineNames] : (byte)0,
                Platform = (start + lay.OffPlatform < data.Length) ? data[start + lay.OffPlatform] : (byte)255,
            };
        }

        private static string ParseName(byte[] data, int start, int fallbackIndex, Layout lay)
        {
            int nameStart = start + lay.OffName;
            int maxLen = Math.Min(NameLen, data.Length - nameStart);
            if (maxLen <= 0) return string.Format("Driver_{0}", fallbackIndex);

            int nullPos = Array.IndexOf(data, (byte)0, nameStart, maxLen);
            int nameLength = (nullPos >= 0) ? nullPos - nameStart : maxLen;
            string raw;
            try
            {
                raw = Encoding.UTF8.GetString(data, nameStart, nameLength).Trim();
            }
            catch
            {
                raw = Encoding.GetEncoding("iso-8859-1").GetString(data, nameStart, nameLength).Trim();
            }
            if (string.IsNullOrWhiteSpace(raw))
                raw = string.Format("Driver_{0}", fallbackIndex);
            return PacketStrings.SanitizePlayerName(raw);
        }

        /// <summary>Backwards-compatible entry point — assumes the 2025 wire format.</summary>
        public static ParticipantsData Parse(byte[] data)
        {
            return Parse(data, 2025);
        }

        /// <summary>
        /// Parses a Participants packet using the byte layout for the given UDP
        /// wire format (<paramref name="packetFormat"/> from the packet header).
        /// v1.1.40 — added 2026 layout support.
        /// v1.1.46 — when header is 2026, probe 2025 vs 2026 body layout (My Team online).
        /// </summary>
        public static ParticipantsData Parse(byte[] data, ushort packetFormat)
        {
            return Parse(data, packetFormat, null);
        }

        /// <param name="bodyWireFormatOverride">
        /// Sticky body layout for the capture (2025 or 2026). When null and the header
        /// is 2026, both layouts are scored and the winner is used.
        /// </param>
        public static ParticipantsData Parse(byte[] data, ushort packetFormat, ushort? bodyWireFormatOverride)
        {
            if (data == null || data.Length < PacketHeader.Size + 1)
                return null;

            ushort bodyFmt = packetFormat >= 2026
                ? (bodyWireFormatOverride ?? ProbeBodyWireFormat(data))
                : (ushort)2025;
            Layout lay = LayoutForBodyWireFormat(bodyFmt);

            int p = PacketHeader.Size;
            byte numActive = data[p];
            int baseOff = p + 1;

            var entries = new ParticipantEntry[GameInfo.MaxSupportedCars];
            var tagsByIdx = new Dictionary<int, string>();
            var nameCount = new Dictionary<string, int>();

            // Parse every entry the buffer can fit, up to MaxSupportedCars.
            // Names are only tracked for active entries (0..numActive-1).
            for (int i = 0; i < GameInfo.MaxSupportedCars; i++)
            {
                int start = baseOff + i * lay.Stride;
                if (start + lay.Stride > data.Length)
                    break;

                var entry = ParseEntry(data, start, i, lay);
                entry.Name = ParseName(data, start, i, lay);
                entries[i] = entry;

                if (i < numActive)
                {
                    string baseName = entry.Name;
                    int count;
                    nameCount.TryGetValue(baseName, out count);
                    nameCount[baseName] = count + 1;
                }
            }

            // Build tags for ACTIVE entries only.
            // Overflow entries (beyond numActive) are NOT used for names because the
            // game fills them with F1 seat names, not real gamertags. Name resolution
            // for overflow entries is handled by LobbyInfo and cross-session recovery.
            for (int i = 0; i < numActive; i++)
            {
                if (entries[i] == null) continue;

                // When showOnlineNames=0 and the entry is a human player, the game
                // sends the F1 seat name (e.g. "Alexander ALBON" for Williams #23)
                // instead of the gamertag. Treat as unreliable and generate a
                // placeholder so LobbyInfo resolution can fill in the real name.
                if (entries[i].ShowOnlineNames == 0 && !entries[i].AiControlled)
                {
                    tagsByIdx[i] = string.Format("Driver_{0}", i);
                    continue;
                }

                string baseName = entries[i].Name;
                int count;
                nameCount.TryGetValue(baseName, out count);
                if (count > 1)
                {
                    int rn = entries[i].RaceNumber;
                    tagsByIdx[i] = rn > 0
                        ? string.Format("{0} #{1}", baseName, rn)
                        : string.Format("{0}_{1}", baseName, i);
                }
                else
                {
                    tagsByIdx[i] = baseName;
                }
            }

            return new ParticipantsData
            {
                NumActiveCars = numActive,
                Entries = entries,
                TagsByCarIdx = tagsByIdx,
            };
        }
    }
}
