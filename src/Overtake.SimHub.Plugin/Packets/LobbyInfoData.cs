using System;
using System.Collections.Generic;
using System.Text;

namespace Overtake.SimHub.Plugin.Packets
{
    public class LobbyInfoEntry
    {
        public bool AiControlled;
        public byte TeamId;
        public byte Nationality;
        public byte Platform;
        public string Name;
        public byte CarNumber;
        public byte YourTelemetry;
        public byte ShowOnlineNames;
        public byte ReadyStatus;
    }

    /// <summary>
    /// Packet ID 9: Lobby Info.
    /// Sent 2/sec while in the multiplayer lobby (before session starts).
    /// Contains the full roster of all players in the lobby.
    ///
    /// IMPORTANT: Lobby slot index does NOT correspond to in-session carIdx.
    /// The lobby uses join-order (or arbitrary) indexing. Use (teamId, carNumber)
    /// to match lobby entries to in-session Participants data.
    ///
    /// F1 25 payload: 1 byte numPlayers + 22 entries of 42 bytes each (954 bytes).
    /// F1 26 will likely carry up to 24 entries (11 teams x 2). The parser reads
    /// up to <see cref="GameInfo.MaxSupportedCars"/> entries; smaller F1 25
    /// buffers are handled by the early-break inside the loop.
    /// </summary>
    public class LobbyInfoData
    {
        private const int Stride = 42;
        private const int NameOffset = 4;
        private const int NameLen = 32;

        public byte NumPlayers;
        public LobbyInfoEntry[] Entries;

        public static LobbyInfoData Parse(byte[] data)
        {
            if (data == null || data.Length < PacketHeader.Size + 1)
                return null;

            int p = PacketHeader.Size;
            byte numPlayers = data[p];
            int baseOff = p + 1;

            var entries = new LobbyInfoEntry[GameInfo.MaxSupportedCars];
            int limit = Math.Min(numPlayers, (byte)GameInfo.MaxSupportedCars);

            for (int i = 0; i < limit; i++)
            {
                int start = baseOff + i * Stride;
                if (start + Stride > data.Length)
                    break;

                var entry = new LobbyInfoEntry
                {
                    AiControlled = data[start + 0] != 0,
                    TeamId = data[start + 1],
                    Nationality = data[start + 2],
                    Platform = data[start + 3],
                };

                int nameStart = start + NameOffset;
                int maxLen = Math.Min(NameLen, data.Length - nameStart);
                string raw = "";
                if (maxLen > 0)
                {
                    int nullPos = Array.IndexOf(data, (byte)0, nameStart, maxLen);
                    int nameLength = (nullPos >= 0) ? nullPos - nameStart : maxLen;
                    try
                    {
                        raw = Encoding.UTF8.GetString(data, nameStart, nameLength).Trim();
                    }
                    catch
                    {
                        raw = Encoding.GetEncoding("iso-8859-1").GetString(data, nameStart, nameLength).Trim();
                    }
                }

                int carNumOff = start + NameOffset + NameLen;
                entry.CarNumber = (carNumOff < data.Length) ? data[carNumOff] : (byte)0;
                entry.YourTelemetry = (carNumOff + 1 < data.Length) ? data[carNumOff + 1] : (byte)0;
                entry.ShowOnlineNames = (carNumOff + 2 < data.Length) ? data[carNumOff + 2] : (byte)0;
                entry.ReadyStatus = (carNumOff + 5 < data.Length) ? data[carNumOff + 5] : (byte)0;

                if (string.IsNullOrWhiteSpace(raw))
                    raw = string.Format("Player #{0}", entry.CarNumber);

                entry.Name = raw;
                entries[i] = entry;
            }

            return new LobbyInfoData
            {
                NumPlayers = numPlayers,
                Entries = entries,
            };
        }
    }
}
