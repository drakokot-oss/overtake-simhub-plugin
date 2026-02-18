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
            var seenTags = new Dictionary<string, int>();

            for (int i = 0; i < numActive; i++)
            {
                int start = baseOff + i * Stride;
                if (start + Stride > data.Length)
                    break;

                var entry = ParseEntry(data, start, i);
                entry.Name = ParseName(data, start, i);

                string tag = entry.Name;
                if (seenTags.ContainsKey(tag))
                {
                    seenTags[tag]++;
                    tag = string.Format("{0}_{1}", entry.Name, i);
                }
                else
                {
                    seenTags[tag] = 1;
                }

                tagsByIdx[i] = tag;
                entries[i] = entry;
            }

            // Rename first occurrence of duplicates
            var baseCounts = new Dictionary<string, List<int>>();
            foreach (var kvp in tagsByIdx)
            {
                string baseTag = kvp.Value;
                int underscorePos = baseTag.LastIndexOf('_');
                if (underscorePos > 0)
                {
                    string suffix = baseTag.Substring(underscorePos + 1);
                    int dummy;
                    if (int.TryParse(suffix, out dummy))
                        baseTag = baseTag.Substring(0, underscorePos);
                }

                List<int> list;
                if (!baseCounts.TryGetValue(baseTag, out list))
                {
                    list = new List<int>();
                    baseCounts[baseTag] = list;
                }
                list.Add(kvp.Key);
            }
            foreach (var kvp in baseCounts)
            {
                if (kvp.Value.Count > 1)
                {
                    foreach (int carIdx in kvp.Value)
                    {
                        if (tagsByIdx[carIdx] == kvp.Key)
                            tagsByIdx[carIdx] = string.Format("{0}_{1}", kvp.Key, carIdx);
                    }
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
                // Only keep entries that look meaningful (non-zero team or non-generic name)
                if (entry.TeamId > 0 || (!entry.Name.StartsWith("Driver_") && !string.IsNullOrWhiteSpace(entry.Name)))
                    overflow[i] = entry;
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
