# Overtake Telemetry — SimHub Plugin

SimHub plugin that captures F1 25 UDP telemetry and exports league-ready JSON files for the Overtake platform.

## Features

- Real-time F1 25 UDP packet capture (port 20777)
- Full session state accumulation (drivers, laps, tyre stints, penalties, pit stops, damage)
- FinalClassification + fallback results (race and qualifying)
- Auto-export JSON on session end
- Manual export via Settings panel button
- League-1.0 JSON schema with enriched enums, awards, and safety car data

## Installation

### Option A: .simhubplugin installer (recommended)

1. Download the latest `OvertakeTelemetry-YYYYMMDD.simhubplugin` file
2. Make sure SimHub is running
3. Double-click the `.simhubplugin` file
4. SimHub will prompt to install — click **Yes**
5. Restart SimHub when prompted

### Option B: Manual DLL copy

1. Copy `Overtake.SimHub.Plugin.dll` to your SimHub installation folder:
   - Default: `C:\Program Files (x86)\SimHub\`
2. Restart SimHub
3. When prompted about the new plugin, click **Enable**

## F1 25 Game Setup

The game must be configured to send UDP telemetry:

1. Open **F1 25** → **Settings** → **Telemetry Settings**
2. Set **UDP Telemetry** to **On**
3. Set **UDP Broadcast Mode** to **Off**
4. Set **UDP IP Address** to `127.0.0.1`
5. Set **UDP Port** to `20777` (must match the plugin's port setting)
6. Set **UDP Send Rate** to `20Hz` or higher
7. Set **UDP Format** to `2025`

## Usage

1. Open SimHub → left menu → **Overtake Telemetry**
2. The **Live Telemetry** section shows:
   - Listener status, packets received
   - Session type, track, active drivers, sessions tracked
3. Start a session in F1 25 — data appears in real time
4. When the session ends, JSON is auto-exported (if enabled)
5. You can also click **Export League JSON** at any time
6. Click **Open Output Folder** to find your exported files

### Settings

| Setting | Default | Description |
|---------|---------|-------------|
| UDP Port | 20777 | Must match F1 25 telemetry port |
| Output Folder | Documents/Overtake/exports | Where JSON files are saved |
| Auto Export | On | Automatically export on session end |

## Building from Source

### Prerequisites

- Windows 10/11
- .NET Framework 4.8 (included in Windows)
- SimHub installed (for reference DLLs)

### Build

```powershell
# Build + test + package (creates .simhubplugin in dist/)
.\scripts\Build-Package.ps1

# Build + package, skip tests
.\scripts\Build-Package.ps1 -SkipTests

# Custom output directory
.\scripts\Build-Package.ps1 -OutputDir "C:\releases"
```

### Tests

```powershell
# SessionStore tests (54 tests)
powershell -ExecutionPolicy Bypass -File scripts\Test-SessionStore.ps1

# Finalizer tests (51 tests)
powershell -ExecutionPolicy Bypass -File scripts\Test-Finalizer.ps1
```

## Architecture

```
Overtake.SimHub.Plugin/
├── OvertakePlugin.cs          # Main plugin entry point (IPlugin, IDataPlugin)
├── OvertakeSettings.cs        # Persisted settings
├── UdpReceiver.cs             # Async UDP listener
├── Packets/                   # Binary packet data classes
│   ├── PacketHeader.cs
│   ├── SessionData.cs
│   ├── LapDataEntry.cs
│   ├── EventData.cs
│   ├── ParticipantsData.cs
│   ├── SessionHistoryData.cs
│   ├── FinalClassificationData.cs
│   └── CarDamageEntry.cs
├── Parsers/
│   └── PacketParser.cs        # Binary → typed objects
├── Store/                     # State accumulation
│   ├── DriverRun.cs
│   ├── SessionRun.cs
│   └── SessionStore.cs
├── Finalizer/                 # JSON export
│   ├── Lookups.cs             # Enum tables (teams, tracks, penalties...)
│   └── LeagueFinalizer.cs     # Store → league-1.0 JSON
└── UI/
    ├── SettingsControl.xaml
    └── SettingsControl.xaml.cs
```

## JSON Output Schema

The exported `league_*.json` follows the `league-1.0` schema:

```json
{
  "schemaVersion": "league-1.0",
  "game": "F1_25",
  "capture": { "sessionUID": "...", "startedAtMs": ..., "endedAtMs": ... },
  "participants": ["Hamilton", "Verstappen", ...],
  "sessions": [
    {
      "sessionType": { "id": 10, "name": "Race" },
      "track": { "id": 5, "name": "Monaco" },
      "results": [ { "position": 1, "tag": "Hamilton", ... } ],
      "drivers": { "Hamilton": { "laps": [...], "tyreStints": [...], ... } },
      "events": [ { "code": "LGOT", "name": "LightsOut", ... } ],
      "awards": { "fastestLap": {...}, "mostConsistent": {...}, "mostPositionsGained": {...} }
    }
  ]
}
```

## License

Proprietary — Overtake Platform.
