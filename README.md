# ⚖️ BleSimpleApp — BLE Sensor Terminal

![C#](https://img.shields.io/badge/-C%23-239120?style=flat-square&logo=c-sharp&logoColor=white)
![.NET MAUI](https://img.shields.io/badge/-.NET_9_MAUI-512BD4?style=flat-square&logo=dotnet&logoColor=white)
![BLE](https://img.shields.io/badge/-Bluetooth_LE-0082FC?style=flat-square&logo=bluetooth&logoColor=white)
![Plugin.BLE](https://img.shields.io/badge/-Plugin.BLE-512BD4?style=flat-square)
![Platforms](https://img.shields.io/badge/-Android_·_iOS_·_Windows_·_macOS-555?style=flat-square)

A cross-platform .NET MAUI app that connects to a microcontroller-based BLE sensor (e.g. an HC-08/HM-10 UART module wired to a load cell) and streams live readings — such as weight — straight into the app, with logging and CSV export.

## ✨ Features

- 🔍 **Scan & connect** to nearby BLE devices from a live device list.
- 🔁 **Automatic reconnection** with retry handling if the connection drops.
- 📟 **Live terminal view** of incoming data, switchable between ASCII and CSV display modes.
- 💾 **CSV logging** of received rows for later analysis/export.
- 📱 **Cross-platform**: Android, iOS, Mac Catalyst, and Windows from a single codebase.
- 🔐 Handles platform-specific Bluetooth/runtime permissions (e.g. Android's runtime Bluetooth permissions) automatically.

## 🏗️ Architecture

| Component | Responsibility |
|---|---|
| `Services/BleService.cs` | Core BLE logic — scanning, connecting, characteristic subscription, automatic reconnect (up to 3 attempts), MTU negotiation, and permission checks. |
| `Services/TerminalLogStore.cs` | Stores and formats incoming data rows for the live log / CSV export. |
| `MainPage.xaml(.cs)` | Main screen — device discovery, connection controls, live data display. |
| `LogsPage.xaml(.cs)` | Dedicated view of the logged/terminal data history. |
| `AppShell.xaml(.cs)` | App navigation shell. |

The BLE layer is built on [Plugin.BLE](https://github.com/dotnet-bluetooth-le/dotnet-bluetooth-le), with built-in support for **HC-08 / HM-10-style UART BLE modules** (the common Bluetooth-to-serial bridges used with Arduino/ESP32-class microcontrollers), recognizing their standard notification characteristics out of the box.

## 📡 Supported Hardware

Designed for any microcontroller setup that streams sensor data (e.g. from a load cell / weight sensor) over a BLE UART module:

- HC-08 / HM-10-compatible BLE-to-serial modules
- Any BLE peripheral exposing a compatible notification characteristic

## 🚀 Getting Started

### Prerequisites
- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- MAUI workload: `dotnet workload install maui`
- A BLE-capable device/emulator to run on (Bluetooth scanning requires a physical device on most platforms)

### Build & Run

```bash
git clone https://github.com/Ali-hey-0/bluetooth_ble_app_weight.git
cd bluetooth_ble_app_weight

# Restore and build
dotnet restore
dotnet build -f net9.0-android   # or net9.0-ios / net9.0-windows10.0.19041.0 / net9.0-maccatalyst
```

Then deploy to a connected device/emulator from Visual Studio or via `dotnet build -t:Run`.

## 📁 Project Structure

```
BleSimpleApp/
├── Platforms/          # Platform-specific implementations
├── Resources/          # Fonts, images, app icons, splash screen
├── Services/
│   ├── BleService.cs           # BLE connection & data management
│   └── TerminalLogStore.cs     # Log storage & formatting
├── MainPage.xaml(.cs)  # Main UI — scan, connect, live data
├── LogsPage.xaml(.cs)  # Terminal/log history view
├── AppShell.xaml(.cs)  # Navigation shell
└── MauiProgram.cs      # App startup & DI configuration
```

## 📄 License

No license file is currently included — all rights reserved by default until one is added.
