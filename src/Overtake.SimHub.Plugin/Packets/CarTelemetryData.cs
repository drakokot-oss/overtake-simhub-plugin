using System;

namespace Overtake.SimHub.Plugin.Packets
{
    /// <summary>
    /// Packet ID 6: Car Telemetry — one CarTelemetryData entry per car. The live Track
    /// Map reads tyre surface/inner temps, brake temps and engine temp.
    ///
    /// FORMAT-AWARE. The 2026 wire QUANTISES m_engineTemperature from uint16 to uint8,
    /// shrinking each entry from 60 → 59 bytes. Confirmed by the official EA 2026 spec
    /// size: PacketCarTelemetry = 1448 bytes = 29 (header) + 24 * 59 + 3 (trailer). Using
    /// the wrong stride drifts every car index > 0 (garbage temps, e.g. engine 26214°C).
    /// brakesTemperature@22 / surface@30 / inner@34 are unchanged (they precede engine).
    ///
    /// Tyre/brake arrays are ordered [RL, RR, FL, FR] (index 0=RL, 1=RR, 2=FL, 3=FR).
    /// Live race UI only (Track Map). Not used by the .otk export pipeline.
    /// Per-car: speed@0 throttle@2 steer@6 brake@10 clutch@14 gear@15 rpm@16 drs@18
    ///   revPct@19 revBits@20 ; u16 brakesTemp[4]@22 ; u8 surf[4]@30 ; u8 inner[4]@34 ;
    ///   2025: u16 engineTemp@38 (entry 60) ; 2026: u8 engineTemp@38 (entry 59).
    /// </summary>
    public class CarTelemetryEntry
    {
        public const int EntrySize2025 = 60;
        public const int EntrySize2026 = 59;
        public const int NumCars = GameInfo.MaxSupportedCars;
        // We only need bytes through engineTemperature (@38..39 in 2025, @38 in 2026).
        private const int MinFields = 39;

        public int CarIdx;
        public int TyreSurfFL, TyreSurfFR, TyreSurfRL, TyreSurfRR;
        public int TyreInnerFL, TyreInnerFR, TyreInnerRL, TyreInnerRR;
        public int BrakeFL, BrakeFR, BrakeRL, BrakeRR;
        public int EngineTemp;
        // For the live speed/throttle/brake/gear charts. Offsets unchanged 2025/2026
        // (they precede the changed fields): speed@0, throttle@2, brake@10, gear@15.
        public int Speed;        // km/h
        public float Throttle;   // 0..1
        public float Brake;      // 0..1
        public int Gear;         // -1..8

        /// <summary>Backwards-compatible entry point — assumes the 2025 wire format.</summary>
        public static CarTelemetryEntry[] Parse(byte[] data)
        {
            return Parse(data, 2025, null);
        }

        public static CarTelemetryEntry[] Parse(byte[] data, ushort packetFormat)
        {
            return Parse(data, packetFormat, null);
        }

        public static CarTelemetryEntry[] Parse(byte[] data, ushort packetFormat, ushort? bodyWireFormatOverride)
        {
            if (data == null || data.Length < PacketHeader.Size + MinFields)
                return null;

            ushort bodyFmt = packetFormat >= 2026
                ? (bodyWireFormatOverride ?? (ushort)2026)
                : (ushort)2025;
            int entrySize = bodyFmt >= 2026 ? EntrySize2026 : EntrySize2025;
            bool engineU8 = bodyFmt >= 2026;

            var entries = new CarTelemetryEntry[NumCars];
            int p = PacketHeader.Size;
            // engineTemperature is the last field we read: u8 @38 in 2026 (needs off+39),
            // u16 @38..39 in 2025 (needs off+40). Make the per-entry guard width-aware so a
            // truncated final entry can never read past the buffer.
            int needed = engineU8 ? 39 : 40;

            for (int i = 0; i < NumCars; i++)
            {
                int off = p + i * entrySize;
                if (off + needed > data.Length)
                    break;

                entries[i] = new CarTelemetryEntry
                {
                    CarIdx = i,
                    Speed = BitConverter.ToUInt16(data, off + 0),
                    Throttle = BitConverter.ToSingle(data, off + 2),
                    Brake = BitConverter.ToSingle(data, off + 10),
                    Gear = (sbyte)data[off + 15],
                    // brakesTemperature[4] (u16) @22 — order RL,RR,FL,FR
                    BrakeRL = BitConverter.ToUInt16(data, off + 22),
                    BrakeRR = BitConverter.ToUInt16(data, off + 24),
                    BrakeFL = BitConverter.ToUInt16(data, off + 26),
                    BrakeFR = BitConverter.ToUInt16(data, off + 28),
                    // tyresSurfaceTemperature[4] (u8) @30
                    TyreSurfRL = data[off + 30],
                    TyreSurfRR = data[off + 31],
                    TyreSurfFL = data[off + 32],
                    TyreSurfFR = data[off + 33],
                    // tyresInnerTemperature[4] (u8) @34
                    TyreInnerRL = data[off + 34],
                    TyreInnerRR = data[off + 35],
                    TyreInnerFL = data[off + 36],
                    TyreInnerFR = data[off + 37],
                    // engineTemperature @38 — u16 in 2025, u8 in 2026
                    EngineTemp = engineU8 ? data[off + 38] : BitConverter.ToUInt16(data, off + 38),
                };
            }

            return entries;
        }
    }
}
