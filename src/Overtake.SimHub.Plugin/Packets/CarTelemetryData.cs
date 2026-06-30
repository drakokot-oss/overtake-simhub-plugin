using System;

namespace Overtake.SimHub.Plugin.Packets
{
    /// <summary>
    /// Packet ID 6: Car Telemetry — one CarTelemetryData entry per car, 60 bytes each.
    /// The live Track Map reads tyre surface/inner temps, brake temps and engine temp.
    ///
    /// The struct is identical in 2025 and 2026 (the 2026 "CarTelemetry2"/packet 16 is a
    /// separate active-aero packet, NOT a change to this one), so a single 60-byte stride
    /// covers both formats — only the car count differs (handled by NumCars + length guard).
    /// Format-agnostic, same as Motion.
    ///
    /// Tyre/brake arrays are ordered [RL, RR, FL, FR] (index 0=RL, 1=RR, 2=FL, 3=FR),
    /// the standard F1 UDP wheel order.
    ///
    /// Live race UI only (Track Map). Not used by the .otk export pipeline.
    /// Layout per car (packed, little-endian):
    ///   u16 speed @0; f throttle @2; f steer @6; f brake @10; u8 clutch @14;
    ///   s8 gear @15; u16 rpm @16; u8 drs @18; u8 revPct @19; u16 revBits @20;
    ///   u16 brakesTemperature[4] @22; u8 tyresSurfaceTemperature[4] @30;
    ///   u8 tyresInnerTemperature[4] @34; u16 engineTemperature @38;
    ///   f tyresPressure[4] @40; u8 surfaceType[4] @56.
    /// </summary>
    public class CarTelemetryEntry
    {
        public const int EntrySize = 60;
        public const int NumCars = GameInfo.MaxSupportedCars;
        // We only need bytes through engineTemperature (@38..39).
        private const int MinFields = 40;

        public int CarIdx;
        public int TyreSurfFL, TyreSurfFR, TyreSurfRL, TyreSurfRR;
        public int TyreInnerFL, TyreInnerFR, TyreInnerRL, TyreInnerRR;
        public int BrakeFL, BrakeFR, BrakeRL, BrakeRR;
        public int EngineTemp;

        public static CarTelemetryEntry[] Parse(byte[] data)
        {
            if (data == null || data.Length < PacketHeader.Size + MinFields)
                return null;

            var entries = new CarTelemetryEntry[NumCars];
            int p = PacketHeader.Size;

            for (int i = 0; i < NumCars; i++)
            {
                int off = p + i * EntrySize;
                if (off + MinFields > data.Length)
                    break;

                entries[i] = new CarTelemetryEntry
                {
                    CarIdx = i,
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
                    // engineTemperature (u16) @38
                    EngineTemp = BitConverter.ToUInt16(data, off + 38),
                };
            }

            return entries;
        }
    }
}
