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

## 🔒 Truly Secure: How Synclo Protects Your Data (End-to-End Architecture)

Synclo is designed from the ground up on a **Zero-Knowledge Model**. This means that **only you** can access your synchronized clipboard data. Neither the developers of Synclo, the servers routing your sync, nor any third-party interceptors can ever read your clipboard history. 

Here is how Synclo guarantees your absolute privacy and seamless security in an easy-to-understand way:

### 1. Locked Before It Leaves (Client-Side Encryption)
The exact millisecond you copy text or image references on Device A, Synclo encrypts it *on your device* before it ever travels over the network.
- **The Server is Blind**: The remote servers only receive, store, and route unreadable encrypted "envelopes". They do not possess the key to unlock them.
- **Safe in Transit**: Even if someone intercepts your network traffic, all they see is random, undecipherable gibberish. Only your authorized devices hold the key to open the envelope.

### 2. Your Password Never Leaves Your Device
When you log in, your master password is never sent to the internet. Instead:
- **The Mathematical Handshake**: Your device performs advanced mathematical computations locally to generate a secure "Authentication Badge" (used solely to log you in) and a separate, local "Wrapping Key" (used to securely lock your actual decryption keys).
- **Zero Trust**: Because the server never knows your password or your wrapping key, it is physically impossible for the server (or a compromised server database) to decrypt your clipboard data.

### 3. Fort Knox for Your Keys (Hardware-Level Protection)
Your actual decryption keys are never stored in standard, vulnerable text or configuration files. Synclo integrates directly with your operating system's native, enterprise-grade secure vaults:
- 🪟 **Windows**: Secured in the **Windows Credential Manager** (using hardware-backed DPAPI).
- 🍏 **macOS**: Secured in the **macOS Keychain** (using Apple's native security framework).
- 🐧 **Linux**: Secured in the **DBus Secret Service** (or a machine-specific hardware-encrypted fallback).

---

<details>
<summary>🛠️ Technical Cryptographic Implementation Details (For Developers & Cryptographers)</summary>

Synclo's cryptographic architecture is powered by standard, industry-vetted protocols:

1. **Key Derivation (KDF)**:
   - **Argon2id** (`Konscious.Security.Cryptography.Argon2`): Derives a base key from the user password and a unique local salt, providing state-of-the-art protection against hardware-accelerated dictionary attacks (GPU/ASIC cracking).
   - **HKDF-SHA256**: Bifurcates the base key into two distinct cryptographically independent keys to prevent key reuse vulnerabilities:
     - `authKey`: Used to authenticate with the API (the raw password is never sent online).
     - `wrappingKey`: Used strictly client-side to secure the Master Key.
2. **Master Key Architecture**:
   - A cryptographically secure random 32-byte (256-bit) Master Key is generated locally.
   - All clipboard entries are encrypted/decrypted client-side using this key with **AES-256-GCM** (authenticated encryption with associated data of `clipboard_v1`).
   - The master key itself is wrapped/encrypted with the `wrappingKey` using **AES-256-GCM** before being backed up to the server.
   - The master key is stored locally inside the OS's native secure vaults (Credential Manager on Windows, Keychain on macOS, Secret Service/Keyring on Linux) and wiped from RAM using `CryptographicOperations.ZeroMemory()` upon logout or key rotation.
</details>


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

