# SerialDebugger

A cross-platform industrial serial port debugger, protocol analyzer, and bench-testing suite built with **.NET 9.0** and **Avalonia UI**. Designed to run natively on **Windows**, **Linux**, and **macOS** from a single codebase or standalone USB executables.

---

## Key Features

### Operation Modes
* **Terminal / Loopback (Port A):** Direct serial communication and single-port hardware loopback testing.
* **SpyPort Proxy (Bridge A & B):** Transparent man-in-the-middle serial sniffer bridging traffic between two physical or virtual ports (`HW <-> PC`).
* **Bench Test (No Bridge):** Independent dual-port bench testing with isolated TX/RX tracking and per-port auto-reply rules.

### Floating Diagnostic Suite
* **Byte Visualizer:** Renders UART frames as color-coded square waveforms—neon green **`START`** bit, alternating red/blue data bits **`0`–`7`** (LSB-first), and magenta **`STOP`** bit—with aligned `ASCII`, `HEX`, `DEC`, and `BIN` readouts beneath each byte.
* **Advanced Packet Monitor:** Displays a timestamped packet table (`ID`, `Time`, `Direction`, `Data (ASCII)`, `Length`) alongside dedicated **`HEX Write Dump (PC -> HW)`** and **`HEX Read Dump (HW -> PC)`** panes.
* **Logic Scope:** Real-time waveform viewer for serial framing and RS-232 control lines.
* **DB9 Wiring & Pinout Guide:** Built-in quick-reference cheat-sheet for RS-232 null-modem, loopback plugs, RS-422 full-duplex, and RS-485 half-duplex pinouts.
* **Pop-Out Traffic Monitor:** Detachable live traffic console for multi-monitor bench setups.

### Hardware Control & Automation
* **1-Click Automated Pass/Fail Diagnostic:** Runs a 20-packet stress sequence (incremental ASCII frames plus `0x55`/`0xAA`/`0x00`/`0xFF` bit-pattern stress frames) while monitoring UART framing, parity, and overrun errors.
* **Line Control & Live Status LEDs:** Manual **`RTS`** and **`DTR`** toggles, **`250ms BREAK`** signal pulse, and live **`CTS`**, **`DSR`**, and **`CD`** indicators.
* **Traffic Injection & Macros:** Send ASCII or HEX payloads with selectable EOL (`CRLF`, `CR`, `LF`, `None`), cyclic auto-repeat timer (`ms`), 4 customizable quick macros, and **`Trap` auto-reply** (e.g., `PING -> PONG`).
* **Session Logging:** Timestamped traffic view with HEX/ASCII toggle, auto-scroll, byte counters, and `.txt` log export.

---

## Project Structure

```text
SerialDebugger/
├── SerialDebugger.sln
├── .gitignore
├── .vscode/
│   ├── launch.json
│   ├── settings.json
│   └── tasks.json
└── SerialDebugger/
    ├── Assets/
    │   └── SpyPortDebugger.ico
    ├── App.axaml / App.axaml.cs
    ├── MainWindow.axaml / MainWindow.axaml.cs
    ├── ByteVisualizerWindow.axaml / ByteVisualizerWindow.axaml.cs
    ├── PacketMonitorWindow.axaml / PacketMonitorWindow.axaml.cs
    ├── ScopeWindow.axaml / ScopeWindow.axaml.cs
    ├── Program.cs
    └── SerialDebugger.csproj
```

---

## Getting Started

### Prerequisites
* [.NET 9.0 SDK (x64)](https://dotnet.microsoft.com/download/dotnet/9.0)

### Run from Source
```bash
dotnet run --project SerialDebugger
```

---

## Building Standalone Cross-Platform Binaries

Publish self-contained, single-file executables (no .NET runtime required on target bench machines):

```powershell
# Windows (x64)
dotnet publish SerialDebugger -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true

# Linux (x64)
dotnet publish SerialDebugger -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true

# macOS (Apple Silicon M1-M4)
dotnet publish SerialDebugger -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true

# macOS (Intel)
dotnet publish SerialDebugger -c Release -r osx-x64 --self-contained true -p:PublishSingleFile=true
```

Compiled executables are output to `SerialDebugger/bin/Release/net9.0/<runtime-id>/publish/`.

### Linux & macOS First-Run Notes
* **Linux:** Copy the binary off FAT32/exFAT USB media onto a local Linux directory, grant execute permissions, and ensure your user has serial port permissions:
  ```bash
  chmod +x ./SerialDebugger
  sudo usermod -aG dialout $USER
  ./SerialDebugger
  ```
* **macOS:** Grant execute permissions and clear the quarantine flag if copied from a USB drive:
  ```bash
  chmod +x ./SerialDebugger
  xattr -d com.apple.quarantine ./SerialDebugger
  ./SerialDebugger
  ```