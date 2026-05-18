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
        private const int Stride = 57;
        private const int NameOffset = 7;
        private const int NameLen = 32;

        public byte NumActiveCars;
        /// <summary>
        /// Slot-indexed array sized to <see cref="GameInfo.MaxSupportedCars"/>.
        /// Entries beyond what the buffer fits will be null.
        /// </summary>
        public ParticipantEntry[] Entries;
        /// <summary>Tags for active entries only (0..NumActiveCars-1).</summary>
        public Dictionary<int, string> TagsByCarIdx;

        private static ParticipantEntry ParseEntry(byte[] data, int start, int i)
        {
            return new ParticipantEntry
            {
                AiControlled = data[start + 0] != 0,
                DriverId = data[start + 1],
                NetworkId = (start + 2 < data.Length) ? data[start + 2] : (byte)255,
                TeamId = data[start + 3],
                MyTeam = data[start + 4] != 0,
                RaceNumber = data[start + 5],
                Nationality = data[start + 6],
                YourTelemetry = (start + 39 < data.Length) ? data[start + 39] : (byte)0,
                ShowOnlineNames = (start + 40 < data.Length) ? data[start + 40] : (byte)0,
                Platform = (start + 43 < data.Length) ? data[start + 43] : (byte)255,
            };
        }

        private static string ParseName(byte[] data, int start, int fallbackIndex)
        {
            int nameStart = start + NameOffset;
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
            return raw;
        }

        public static ParticipantsData Parse(byte[] data)
        {
            if (data == null || data.Length < PacketHeader.Size + 1)
                return null;

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
                int start = baseOff + i * Stride;
                if (start + Stride > data.Length)
                    break;

                var entry = ParseEntry(data, start, i);
                entry.Name = ParseName(data, start, i);
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
