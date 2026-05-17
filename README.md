# Synclo

[![Build Status](https://img.shields.io/badge/build-passing-brightgreen.svg)](#)
[![Framework](https://img.shields.io/badge/framework-.NET%2010.0-blue.svg)](https://dotnet.microsoft.com/)
[![UI Library](https://img.shields.io/badge/UI-Avalonia%2011.3-orange.svg)](https://avaloniaui.net/)
[![Database](https://img.shields.io/badge/database-SQLite-blue.svg)](https://sqlite.org/)
[![License](https://img.shields.io/badge/license-GPL--3.0-green.svg)](LICENSE)

**Synclo** is a secure, real-time, cross-platform desktop clipboard synchronization utility built using **Avalonia UI** and **.NET 10**. It runs quietly in the background, automatically syncing copied text and clipboard history across all your logged-in devices. Engineering highlights include **end-to-end client-side encryption**, **fully platform-native OS services** (for clipboard monitoring, secure storage, and boot autostart), and a **highly optimized SQLite database pipeline** that isolates I/O tasks to avoid database locking.

---

## Key Features

- **⚡ Real-Time WebSocket Synchronization**: Utilizes high-performance WebSockets (`IWebSocketService`) for instantaneous clipboard propagation across connected client devices.
- **🔒 End-to-End Cryptography**: Implements military-grade client-side encryption. The remote servers never see your plaintext data or master password.
- **💾 Offline-First Architecture**: Stores histories in a local SQLite database using a highly optimized database-isolated thread-scheduler (`ConcurrentExclusiveSchedulerPair`) to ensure seamless offline functionality.
- **🖥️ Platform-Native Integrations**:
  - **Windows**: Low-overhead Win32 message window hooking (`WM_CLIPBOARDUPDATE`) and secure storage in Windows Credential Manager (DPAPI).
  - **macOS**: Native Keychain storage via direct P/Invoke into the macOS `Security` and `CoreFoundation` frameworks.
  - **Linux**: Integration with DBus Secret Service (`org.freedesktop.secrets`) alongside a secure GCM-encrypted file backup using machine-specific keys.
- **🔄 Bounded Synchronization Channel Pipeline**: Uses a `Channel<ClipboardPipelineEvent>` model to gracefully process outgoing and incoming clipboard updates without dropping events or causing UI blockages.
- **🗑️ Advanced Deletion (Tombstones)**: Realizes remote deletion synchronization via tombstone entries in the local database to handle updates that occur while a device is offline.
- **🚀 Seamless Boot Startup**: Native auto-run configuration (Registry on Windows, `.desktop` launcher on Linux, plist LaunchAgents on macOS).
- **🎨 Premium Dark & Light UI**: A modern interface featuring responsive layouts, custom smooth scrolling, infinite scroll loading, and system tray integration (minimize-to-tray and silent startup).

---

## Security Design Overview

Synclo is architected on a **Zero-Knowledge** model. Your credentials and synced content are secured through multiple cryptographic layers:

1. **Key Derivation (KDF)**:
   - Powered by **Argon2id** (`Konscious.Security.Cryptography.Argon2`).
   - Derives a base key from the user password and a unique salt.
   - Leverages **HKDF-SHA256** to bifurcate the base key into two distinct keys:
     - `authKey`: Used to authenticate with the API (the raw password is never sent online).
     - `wrappingKey`: Used to secure the Master Key.
2. **Master Key Architecture**:
   - A cryptographically secure random 32-byte key is generated locally.
   - All clipboard entries are encrypted/decrypted client-side using this key with **AES-256-GCM** (authenticated encryption with associated data of `clipboard_v1`).
   - The master key itself is wrapped/encrypted with the `wrappingKey` using **AES-256-GCM** before being backed up to the server.
   - The master key is stored locally inside the OS's native secure vaults (Credential Manager on Windows, Keychain on macOS, Secret Service/Keyring on Linux).


---

## Getting Started

### Prerequisites

- **SDK**: [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or higher.
- **OS Platform Utilities**:
  - **Linux**: DBus daemon and `libsecret` library (most desktop distributions like Ubuntu, Fedora, or Arch have this out of the box).
  - **macOS**: macOS 10.15+ (Catalina or higher).
  - **Windows**: Windows 10/11 build 17763+.

### Running Locally

1. **Clone the repository**:
   ```bash
   git clone https://github.com/synclo-app/Synclo-Desktop.git
   cd Synclo-Desktop
   ```

2. **Restore dependencies**:
   ```bash
   dotnet restore
   ```

3. **Build the application**:
   ```bash
   dotnet build
   ```

4. **Run the application**:
   ```bash
   dotnet run
   ```
   *To run the application in the background (silent autostart simulation):*
   ```bash
   dotnet run -- --autostart
   ```

---

## License

This project is licensed under the GPL-3.0 License. Feel free to use, modify, and distribute according to the license terms.

