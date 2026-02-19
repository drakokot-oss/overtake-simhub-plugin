using System;
using System.Collections.Generic;
using System.Text;

namespace Overtake.SimHub.Plugin.Packets
{
    public class ParticipantEntry
    {
        public byte DriverId;
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
    /// Payload: 1 byte numActiveCars + 22 entries of 57 bytes each.
    /// Name field: 32 bytes at offset 7 within each entry (null-terminated, UTF-8/Latin-1).
    /// </summary>
    public class ParticipantsData
    {
        private const int Stride = 57;
        private const int NameOffset = 7;
        private const int NameLen = 32;

        private const int MaxCars = 22;

        public byte NumActiveCars;
        public ParticipantEntry[] Entries;
        public Dictionary<int, string> TagsByCarIdx;
        /// <summary>
        /// Entries parsed beyond numActiveCars (player recovery).
        /// </summary>
        public Dictionary<int, ParticipantEntry> OverflowEntries;

        private static ParticipantEntry ParseEntry(byte[] data, int start, int i)
        {
            return new ParticipantEntry
            {
                AiControlled = data[start + 0] != 0,
                DriverId = data[start + 1],
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

            var entries = new ParticipantEntry[numActive];
            var tagsByIdx = new Dictionary<int, string>();
            var nameCount = new Dictionary<string, int>();

            for (int i = 0; i < numActive; i++)
            {
                int start = baseOff + i * Stride;
                if (start + Stride > data.Length)
                    break;

                var entry = ParseEntry(data, start, i);
                entry.Name = ParseName(data, start, i);
                entries[i] = entry;

                string baseName = entry.Name;
                int count;
                nameCount.TryGetValue(baseName, out count);
                nameCount[baseName] = count + 1;
            }

            // Build tags: use race number for disambiguation of duplicate names
            for (int i = 0; i < numActive; i++)
            {
                if (entries[i] == null) continue;
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

            // Parse overflow entries (beyond numActive) for player recovery
            var overflow = new Dictionary<int, ParticipantEntry>();
            for (int i = numActive; i < MaxCars; i++)
            {
                int start = baseOff + i * Stride;
                if (start + Stride > data.Length)
                    break;
                var entry = ParseEntry(data, start, i);
                entry.Name = ParseName(data, start, i);
                if (entry.TeamId == 255 && entry.Name.StartsWith("Driver_")) continue;
                if (entry.TeamId > 0 || (!entry.Name.StartsWith("Driver_") && !string.IsNullOrWhiteSpace(entry.Name)))
                {
                    // Dedup overflow names against existing tags
                    string tag = entry.Name;
                    int dupCount;
                    nameCount.TryGetValue(tag, out dupCount);
                    nameCount[tag] = dupCount + 1;
                    if (dupCount > 0)
                    {
                        int rn = entry.RaceNumber;
                        tag = rn > 0
                            ? string.Format("{0} #{1}", entry.Name, rn)
                            : string.Format("{0}_{1}", entry.Name, i);
                    }
                    tagsByIdx[i] = tag;
                    overflow[i] = entry;
                }
            }

            return new ParticipantsData
            {
                NumActiveCars = numActive,
                Entries = entries,
                TagsByCarIdx = tagsByIdx,
                OverflowEntries = overflow,
            };
        }
    }
}
