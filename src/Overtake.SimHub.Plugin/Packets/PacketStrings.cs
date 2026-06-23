using System.Text;

namespace Overtake.SimHub.Plugin.Packets
{
    /// <summary>
    /// Shared string cleanup for UDP name fields (Participants, LobbyInfo).
    /// Strips ASCII control characters that occasionally appear when the wire
    /// layout shifts by a byte (e.g. "\tPRT_martbryt" with a leading TAB).
    /// </summary>
    public static class PacketStrings
    {
        public static string SanitizePlayerName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;

            var sb = new StringBuilder(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (!char.IsControl(c))
                    sb.Append(c);
            }
            string cleaned = sb.ToString().Trim();
            return string.IsNullOrEmpty(cleaned) ? raw.Trim() : cleaned;
        }
    }
}
