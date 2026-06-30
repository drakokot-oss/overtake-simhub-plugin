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
        public MotionEntry[] Motion;
        public LapDataEntry[] LapData;
        public EventData Event;
        public ParticipantsData Participants;
        public FinalClassificationData FinalClassification;
        public LobbyInfoData LobbyInfo;
        public CarStatusEntry[] CarStatus;
        public CarTelemetryEntry[] CarTelemetry;
        public CarDamageEntry[] CarDamage;
        public SessionHistoryData SessionHistory;

        /// <summary>
        /// Raw packet bytes, preserved so the store can sample them when the UDP
        /// wire format is one the parsers do not support yet (Phase 2 enabler,
        /// v1.1.39). Always set by <see cref="PacketParser.Dispatch"/>.
        /// </summary>
        public byte[] RawData;
    }

    /// <summary>
    /// Central dispatcher: parses header, then routes to the correct parser
    /// based on packetId. Returns null for unknown/unsupported packet types.
    /// </summary>
    public static class PacketParser
    {
        public static ParsedPacket Dispatch(byte[] data)
        {
            return Dispatch(data, null);
        }

        /// <param name="bodyWireFormatOverride">
        /// Sticky 2025/2026 body layout for captures whose header says 2026 but the
        /// payload still uses the legacy stride (My Team online). Null = probe per packet.
        /// </param>
        public static ParsedPacket Dispatch(byte[] data, ushort? bodyWireFormatOverride)
        {
            var header = PacketHeader.Parse(data);
            if (header == null)
                return null;

            var result = new ParsedPacket { Header = header, RawData = data };

            switch (header.PacketId)
            {
                case 0:
                    result.Motion = MotionEntry.Parse(data);
                    break;
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
                    result.Participants = ParticipantsData.Parse(data, header.PacketFormat, bodyWireFormatOverride);
                    break;
                case 6:
                    result.CarTelemetry = CarTelemetryEntry.Parse(data);
                    break;
                case 7:
                    result.CarStatus = CarStatusEntry.Parse(data, header.PacketFormat, bodyWireFormatOverride);
                    break;
                case 8:
                    result.FinalClassification = FinalClassificationData.Parse(data);
                    break;
                case 9:
                    result.LobbyInfo = LobbyInfoData.Parse(data, header.PacketFormat, bodyWireFormatOverride);
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
