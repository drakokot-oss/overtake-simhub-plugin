namespace Overtake.SimHub.Plugin
{
    public class OvertakeSettings
    {
        public int UdpPort;
        public int ForwardPort;
        public string OutputFolder;
        public bool AutoExportJson;
        public string LastExportPath;

        public OvertakeSettings()
        {
            UdpPort = 20778;
            ForwardPort = 20777;
            OutputFolder = "";
            AutoExportJson = true;
            LastExportPath = "";
        }
    }
}
