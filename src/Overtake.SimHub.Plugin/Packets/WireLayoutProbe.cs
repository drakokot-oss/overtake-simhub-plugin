using System;

namespace Overtake.SimHub.Plugin.Packets
{
    /// <summary>
    /// F1 26 can report packetFormat=2026 in the header while the payload body still
    /// uses the 2025 entry layout (My Team online lobbies). Career / AI grids use the
    /// true 2026 stride. When the header says 2026 we probe both layouts and pick the
    /// higher-scoring one, then stick to it for the rest of the capture.
    /// </summary>
    public static class WireLayoutProbe
    {
        public static ushort? TryProbe(byte[] data, int packetId)
        {
            if (data == null) return null;
            switch (packetId)
            {
                case 4: return ParticipantsData.ProbeBodyWireFormat(data);
                case 9: return LobbyInfoData.ProbeBodyWireFormat(data);
                case 7: return CarStatusEntry.ProbeBodyWireFormat(data);
                default: return null;
            }
        }

        public static ushort ResolveBodyWireFormat(ushort headerFormat, byte[] data, int packetId, ushort? sticky)
        {
            if (headerFormat < 2026) return 2025;
            if (sticky.HasValue) return sticky.Value;
            ushort? probed = TryProbe(data, packetId);
            return probed ?? 2026;
        }
    }
}
