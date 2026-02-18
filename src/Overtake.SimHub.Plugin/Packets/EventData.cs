using System;
using System.Text;

namespace Overtake.SimHub.Plugin.Packets
{
    /// <summary>
    /// Packet ID 3: Event Data.
    /// Payload starts at offset 29. First 4 bytes = ASCII event code.
    /// Event-specific data follows at payload offset 4.
    /// </summary>
    public class EventData
    {
        public string Code;

        // OVTK
        public byte OvertakerIdx;
        public byte OvertakenIdx;

        // PENA
        public byte PenaltyType;
        public byte InfringementType;
        public byte VehicleIdx;
        public byte OtherVehicleIdx;
        public byte TimeSec;
        public byte LapNum;
        public byte PlacesGained;

        // COLL
        public byte Vehicle1Idx;
        public byte Vehicle2Idx;

        // RTMT
        public byte RetiredVehicleIdx;
        public byte RetiredReason;

        // FTLP
        public byte FastestLapVehicleIdx;
        public float FastestLapTimeSec;

        // SCAR
        public byte SafetyCarType;
        public byte SafetyCarEventType;

        public static EventData Parse(byte[] data)
        {
            if (data == null || data.Length < PacketHeader.Size + 4)
                return null;

            int p = PacketHeader.Size;
            string code = Encoding.ASCII.GetString(data, p, 4);

            if (code == "BUTN")
                return null;

            var evt = new EventData { Code = code };
            int d = p + 4;

            if (code == "OVTK" && data.Length >= d + 2)
            {
                evt.OvertakerIdx = data[d];
                evt.OvertakenIdx = data[d + 1];
            }
            else if (code == "PENA" && data.Length >= d + 7)
            {
                evt.PenaltyType = data[d];
                evt.InfringementType = data[d + 1];
                evt.VehicleIdx = data[d + 2];
                evt.OtherVehicleIdx = data[d + 3];
                evt.TimeSec = data[d + 4];
                evt.LapNum = data[d + 5];
                evt.PlacesGained = data[d + 6];
            }
            else if (code == "PENA" && data.Length >= d + 3)
            {
                evt.PenaltyType = data[d];
                evt.InfringementType = data[d + 1];
                evt.VehicleIdx = data[d + 2];
            }
            else if (code == "COLL" && data.Length >= d + 2)
            {
                evt.Vehicle1Idx = data[d];
                evt.Vehicle2Idx = data[d + 1];
            }
            else if (code == "RTMT" && data.Length >= d + 2)
            {
                evt.RetiredVehicleIdx = data[d];
                evt.RetiredReason = data[d + 1];
            }
            else if (code == "FTLP" && data.Length >= d + 5)
            {
                evt.FastestLapVehicleIdx = data[d];
                evt.FastestLapTimeSec = BitConverter.ToSingle(data, d + 1);
            }
            else if (code == "SCAR" && data.Length >= d + 2)
            {
                evt.SafetyCarType = data[d];
                evt.SafetyCarEventType = data[d + 1];
            }

            return evt;
        }
    }
}
