# AgValoniaGPS

Cross-platform rewrite of [AgOpenGPS](https://github.com/farmerbriantee/AgOpenGPS) using Avalonia, .NET 10, and C#.  
The app uses MVVM + DI, with most logic in `Shared/` and thin platform shells in `Platforms/`.

## GNSS / RTK Features

- GNSS parsing pipeline with checksum validation for:
  - `$PANDA`, `$PAOGI`
  - Standard NMEA `$GxGGA`, `$GxRMC`, `$GxGSA`, `$GxGSV`
- RTK/NTRIP client with:
  - caster auth + mountpoint support
  - periodic GGA sending
  - RTCM forwarding over UDP (`2233`) to machine modules
- NTRIP profile management:
  - multiple profiles in JSON
  - default profile + per-field associations
  - optional auto-connect on field load
- GNSS hardware links:
  - USB/serial GNSS (Desktop)
  - BLE GNSS over Nordic UART Service (Desktop)
  - BLE service on iOS is currently a stub (placeholder for CoreBluetooth implementation)
- RTCM fan-out from NTRIP to connected GNSS links (serial/BLE) in ViewModel layer
- Single/dual antenna heading configuration with:
  - dual heading offset
  - fix-to-fix fallback
  - IMU heading fusion
- RTK quality gates:
  - minimum fix quality
  - maximum HDOP
  - maximum differential age
- Built-in simulator mode for guidance/autosteer testing without physical GNSS hardware

## GNSS Platform Matrix

| Capability | Desktop (Windows/macOS/Linux) | Android | iOS |
|---|---|---|---|
| NMEA parsing (`PANDA/PAOGI`, `GGA/RMC/GSA/GSV`) | Yes | Yes | Yes |
| NTRIP client (caster + mountpoint + GGA) | Yes | Yes | Yes |
| RTCM forward to UDP modules (`2233`) | Yes | Yes | Yes |
| USB/Serial GNSS input | Yes | No | No |
| BLE GNSS input | Partial (Windows/Linux via InTheHand, macOS via native CoreBluetooth service) | No | Partial (service registered, currently stub) |
| RTCM fan-out to connected Serial/BLE GNSS | Yes (when corresponding link is connected) | No | No |
| UDP module communication (steer/machine/IMU/GPS) | Yes | Yes | Yes |
| Simulator GNSS mode | Yes | Yes | Yes |

## Guidance / Field Features

- Zero-copy AutoSteer pipeline (`GPS -> parse -> guidance -> PGN`) optimized for low latency
- UDP communication with AgOpenGPS-style modules (steer/machine/IMU/GPS)
- Section control, coverage map painting, boundary handling, headland generation, and U-turn workflow
- Tile map service and shared map rendering abstractions across platforms

## Platforms

- Windows (x64)
- macOS (x64, ARM64)
- Linux (x64)
- Android
- iOS

## Tech Stack

- **UI:** Avalonia 12, ReactiveUI
- **Runtime:** .NET 10
- **Architecture:** MVVM with dependency injection
- **Testing:** NUnit, Avalonia.Headless

## Build (Quick Start)

Prerequisites: .NET 10 SDK

```bash
dotnet restore AgValoniaGPS.sln

# Desktop (Windows/macOS/Linux)
dotnet build Platforms/AgValoniaGPS.Desktop/AgValoniaGPS.Desktop.csproj

# Run Desktop
dotnet run --project Platforms/AgValoniaGPS.Desktop

# Android
dotnet build Platforms/AgValoniaGPS.Android/AgValoniaGPS.Android.csproj

# iOS (simulator build)
dotnet build Platforms/AgValoniaGPS.iOS/AgValoniaGPS.iOS.csproj
```

See [BUILD.md](BUILD.md) for platform-specific setup, runtime identifiers, and publishing.

## Tests

```bash
# Full suite
dotnet test AgValoniaGPS.sln

# Focused suites
dotnet test Tests/AgValoniaGPS.Models.Tests
dotnet test Tests/AgValoniaGPS.Services.Tests
dotnet test Tests/AgValoniaGPS.UI.Tests
```

More details: [Docs/TESTING.md](Docs/TESTING.md)

## Protocol / Architecture Docs

- [PGN.md](PGN.md) - UDP/PGN protocol details
- [BUILD.md](BUILD.md) - build/publish instructions
- [CONTRIBUTING.md](CONTRIBUTING.md) - project structure and contribution guide

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

[GNU General Public License v3.0](LICENSE.md)
