# Synclo

[![Build Status](https://img.shields.io/badge/build-passing-brightgreen.svg)](#)
[![Framework](https://img.shields.io/badge/framework-.NET%2010.0-blue.svg)](https://dotnet.microsoft.com/)
[![UI Library](https://img.shields.io/badge/UI-Avalonia%2011.3-orange.svg)](https://avaloniaui.net/)
[![Database](https://img.shields.io/badge/database-SQLite-blue.svg)](https://sqlite.org/)
[![License](https://img.shields.io/badge/license-GPL--3.0-green.svg)](LICENSE)

**Synclo** is a secure, cross-platform clipboard sync app that keeps your copied text instantly available across all your devices.

Copy something on one of your devices and paste it on your other device instantly. Synclo runs quietly in the background, syncs in real time, stores your clipboard history locally, and keeps everything protected with true end-to-end encryption.

Built with **Avalonia UI** and **.NET 10**, Synclo is designed to feel lightweight, native, and reliable on Windows, Linux, and macOS.

---

## Features

### 🔄 Instant Clipboard Sync

Clipboard updates are synced across your connected devices in real time using persistent WebSocket connections.

### 🔒 End-to-End Encryption

Your clipboard data is encrypted on-device before it ever leaves your system. Synclo servers never have access to your plaintext clipboard history or encryption keys.

### 💾 Clipboard History

Access previously copied items even when offline. Clipboard history is stored locally using SQLite for fast and reliable performance.

### 📌 Clipboard Pinning

Pin frequently used items to the top of your history list. Pinned items behave as a stack (most recently pinned on top) and are preserved during bulk history clearing. Pin status changes synchronize in real time across all of your active devices.

### 🖥️ Native Platform Integration

Synclo integrates directly with platform-native APIs for clipboard monitoring, secure credential storage, and automatic startup.

* **Windows**

  * Native clipboard hooks (`WM_CLIPBOARDUPDATE`)
  * Windows Credential Manager support
  * Startup via Registry

* **macOS**

  * Native Keychain integration
  * LaunchAgent startup support

* **Linux**

  * Secret Service / Keyring integration
  * `.desktop` autostart support

### ⚡ Lightweight Background Operation

Designed to stay out of the way with:

* Silent startup support
* Minimize-to-tray behavior
* Efficient event pipelines
* Optimized database handling to prevent UI slowdowns

### 🗑️ Smart Sync & Deletion Handling

Clipboard deletions sync properly across devices, including devices that were temporarily offline.

### 🎨 Modern UI

Responsive dark and light themes with smooth scrolling, infinite history loading, and a clean desktop-focused interface. Copying content is simplified: clicking anywhere on a history card copies it directly to your clipboard, and you can easily toggle pin status directly on each card.

---

## Security

Synclo follows a **zero-knowledge architecture**.

Your encryption keys are generated and managed locally on your device. Clipboard content is encrypted using **AES-256-GCM**, while passwords are protected using **Argon2id**-based key derivation.

The server only stores encrypted blobs and cannot read your clipboard data.

### Security Highlights

* AES-256-GCM authenticated encryption
* Argon2id password-based key derivation
* HKDF-SHA256 key separation
* Locally generated master encryption keys
* Native OS secure vault storage

---

<details>
<summary>🛠️ Technical Cryptographic Implementation Details</summary>

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

## Supported Platforms

* Windows 10/11
* Linux
* macOS 10.15+

---

## Getting Started

### Prerequisites

* [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

Additional Linux dependencies:

* DBus
* `libsecret`

---

## Running Locally

### Clone the repository

```bash
git clone https://github.com/synclo-app/Synclo-Desktop.git
cd Synclo-Desktop
```

### Restore dependencies

```bash
dotnet restore
```

### Build the application

```bash
dotnet build
```

### Run the application

```bash
dotnet run
```

### Run in background mode

```bash
dotnet run -- --autostart
```

---

## License

This project is licensed under the GPL-3.0 License. Feel free to use, modify, and distribute according to the license terms.
