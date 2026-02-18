using Overtake.SimHub.Plugin.Packets;

namespace Overtake.SimHub.Plugin.Parsers
{
    /// <summary>
    /// Result of dispatching a raw UDP packet through the appropriate parser.
    /// Exactly one of the typed fields will be non-null.
    /// </summary>
    public class ParsedPacket
    {
        public PacketHeader Header;
        public SessionData Session;
        public LapDataEntry[] LapData;
        public EventData Event;
        public ParticipantsData Participants;
        public FinalClassificationData FinalClassification;
        public CarDamageEntry[] CarDamage;
        public SessionHistoryData SessionHistory;
    }

    /// <summary>
    /// Central dispatcher: parses header, then routes to the correct parser
    /// based on packetId. Returns null for unknown/unsupported packet types.
    /// </summary>
    public static class PacketParser
    {
        public static ParsedPacket Dispatch(byte[] data)
        {
            var header = PacketHeader.Parse(data);
            if (header == null)
                return null;

            var result = new ParsedPacket { Header = header };

            switch (header.PacketId)
            {
                case 1:
                    result.Session = SessionData.Parse(data);
                    break;
                case 2:
                    result.LapData = LapDataEntry.Parse(data);
                    break;
                case 3:
                    result.Event = EventData.Parse(data);
                    break;
                case 4:
                    result.Participants = ParticipantsData.Parse(data);
                    break;
                case 8:
                    result.FinalClassification = FinalClassificationData.Parse(data);
                    break;
                case 10:
                    result.CarDamage = CarDamageEntry.Parse(data);
                    break;
                case 11:
                    result.SessionHistory = SessionHistoryData.Parse(data);
                    break;
                default:
                    return result;
            }

            return result;
        }
    }
}
