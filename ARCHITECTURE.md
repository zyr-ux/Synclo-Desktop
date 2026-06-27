# Synclo-Desktop Architecture

This document details the internal architecture, design patterns, and platform-native integration layers of **Synclo-Desktop**. Synclo is a secure, cross-platform clipboard synchronization client engineered in C# using .NET 10 and Avalonia UI.

---

## 🗺️ Architectural Overview

Synclo is built around the **Model-View-ViewModel (MVVM)** pattern, featuring high-speed real-time communication via WebSockets, persistent local synchronization history using SQLite, and hardware-integrated platform hooks for cross-platform utility.

```text
┌─────────────────────────────────────────────────────────────────────────┐
│                        UI Layer (Avalonia MVVM)                         │
│   ┌──────────────┐         Compiled Bindings        ┌──────────────┐    │
│   │ Views: XAML  │ <──────────────────────────────> │  ViewModels  │    │
│   └──────────────┘                                  └──────────────┘    │
│                                                            ▲            │
│   ┌──────────────────┐                                     │            │
│   │ ViewModelFactory │ ─── Dependency Injection ───────────┘            │
│   └──────────────────┘                                                  │
└─────────────────────────────────────────────────────────────────────────┘
                                    │ (Initializes)
                                    ▼
┌─────────────────────────────────────────────────────────────────────────┐
│                        Service Orchestration Layer                      │
│   ┌─────────────────┐                                                   │
│   │ AppBootstrapper │ ─── Wires Event Listeners ───┐                    │
│   └─────────────────┘                              ▼                    │
│                                         ┌──────────────────────┐        │
│                                         │ ClipboardSyncService │        │
│                                         └──────────────────────┘        │
│   ┌─────────────────┐                              │                    │
│   │ SettingsService │ ─── Manages Configuration ───┤                    │
│   └─────────────────┘                              ▼                    │
│                                         ┌──────────────────────┐        │
│                                         │ CryptographyService  │        │
│                                         └──────────────────────┘        │
└─────────────────────────────────────────────────────────────────────────┘
         │                                   │                  │
         │ (Queries Storage)                 │ (Listens)        │ (Syncs)
         ▼                                   ▼                  ▼
┌─────────────────────────┐       ┌────────────────────┐ ┌──────────────┐
│ Platform secure storage │       │ IClipboardMonitor  │ │ Data/Network │
│  (Keychain, DPAPI, DBus)│       │ (Windows, macOS,   │ │ (WS, SQLite, │
└─────────────────────────┘       │  Linux listener)   │ │  REST API)   │
                                  └────────────────────┘ └──────────────┘
```

### 📁 Repository Structure

```tree
Synclo-Desktop/
├── App.axaml                 # Avalonia Application structure (Tray icon, styling)
├── App.axaml.cs              # Application lifecycle, Single Instance & Autostart checks
├── Program.cs                # Entry point, STA thread initiation
├── ViewLocator.cs            # Avalonia MVVM View-to-ViewModel resolver
├── app.manifest              # Windows execution permissions manifest
├── Synclo.csproj             # .NET 10 Project file & dependencies
├── Synclo.sln                # Visual Studio / Rider Solution
│
├── Assets/                   # Visual resources (App icon, SVGs)
├── Themes/                   # Fluent theme toggling and styling definitions
├── Models/                   # Data DTOs (AppSettings, HistoryItemModel, DTO Requests/Responses)
├── Behaviors/                # Interactive UI behaviors (Smooth scroll, Infinite loading)
├── Converters/               # Custom XAML binding converters (OS type, icon type)
│
├── Utilities/                # Core helper utilities & factory layers
│   ├── ApplicationControlService.cs # App runtime state control
│   ├── CryptographyService.cs       # Argon2 & AES encryption logic
│   ├── DependencyInjection.cs       # DI configurations
│   ├── SingleInstanceManager.cs     # Single instance verification
│   ├── Utils.cs                     # General helper methods
│   └── ViewModelFactory.cs          # Dependency-Injected ViewModel instantiation factory
│
├── Features/                 # Core business logic feature slices
│   ├── Clipboard_Manager/    # Clipboard Monitor hooks, factories, and SQLite sync services
│   ├── Connection_Monitor/   # Online connection detection
│   ├── Dialog_Manager/       # Modally spawned dialog controls (Confirmation, Reset Password)
│   ├── Network_Services/     # REST Endpoint calls, Session validation, WebSocket streams
│   ├── Notifications_Manager/# Desktop notifications services
│   ├── Secrets_Manager/      # Platform-native secure secret stores & factory
│   ├── Settings_Manager/     # App configurations loader
│   └── Startup_Manager/      # Platform-native autostart management & factory
│
├── ViewModels/               # MVVM ViewModels (Home, Settings, Account, Login, Dialogs)
└── Views/                    # Avalonia XAML Views (MainWindow, Views per VM, Components)
```

---

## ⚡ Real-Time Clipboard Sync Pipeline

The synchronization engine (`ClipboardSyncService`) acts as a unified coordinator. To prevent UI lockups and deadlocks, Synclo leverages a **Producer-Consumer pipeline** utilizing .NET's high-performance memory-bounded channels.

### 1. Unified Event Channel
A single channel `Channel<ClipboardPipelineEvent>` receives both local updates (captured from the local OS) and remote updates (received over the WebSocket connection).
- **Bounded Channel Options**: Configured with a `MaxQueuedClipboardEvents = 100` limit and a `FullMode = BoundedChannelFullMode.Wait`. This ensures that even during high throughput (e.g., rapid copy-pasting), events (specifically delete tombstones) are never dropped.
- **Sequenced Execution**: A persistent consumer thread `ProcessClipboardChannelAsync` reads events from the channel sequentially. This prevents race conditions where a remote write and a local read collide.

### 2. Feedback Suppression Guard
Without a guard, setting a remote clipboard value on the local operating system would trigger a local "Clipboard Changed" event, which would sync back to the server and propagate indefinitely.
- Synclo handles this using a volatile `_isProcessingRemoteUpdate` suppression flag.
- When writing a remote entry to the OS clipboard, the guard is flagged as `true`.
- The local clipboard listener checks this flag: if active, the local copy is ignored, breaking the feedback loop.

```text
A. OUTGOING FLOW (Local Copy -> Server Sync)
========================================================================================
1. [User Copy]        User copies text to operating system clipboard.
2. [OS Clipboard]     ──(Clipboard Update Notification)──> [IClipboardMonitor]
3. [IClipboardMonitor] ──(Raises OnClipboardChanged Event)──> [ClipboardSyncService]
4. [Sync Service]     Encrypts payload with AES-256-GCM.
5. [Sync Service]     ──(Writes Encrypted Payload)──> [ClipboardRepository (SQLite)]
6. [Sync Service]     ──(Sends Payload Frame)──> [WebSocketService] ──> [Remote Server]
7. [Remote Server]    ──(Sends ACK Frame back)──> [WebSocketService]
8. [Sync Service]     Marks local database entry as "Synced = 1".
========================================================================================

B. INCOMING FLOW (Remote Update -> Local Paste)
========================================================================================
1. [Remote Server]    ──(Pushes WebSocket Event Frame)──> [WebSocketService]
2. [WebSocketService] ──(Triggers OnMessageReceived)──> [ClipboardSyncService]
3. [Sync Service]     Decrypts ciphertext payload using AES-256-GCM.
4. [Sync Service]     ──(Writes Decrypted Payload)──> [ClipboardRepository (SQLite)]
5. [Sync Service]     Activates suppression guard (_isProcessingRemoteUpdate = true).
6. [Sync Service]     ──(Writes Plaintext Text)──> [OS Clipboard]
7. [Sync Service]     Deactivates suppression guard (_isProcessingRemoteUpdate = false).
========================================================================================
```

### 3. Delta Sync & Hard Recoveries
When the app launches or reconnects:
- **Delta Sync**: It performs a delta synchronization via `GetClipboardSyncAsync(since, limit, offset)`, requesting entries modified since the cached `last_sync` timestamp.
- **Tombstones**: Deletion updates are received as tombstone models (`is_deleted = 1`). The local repository flags matching entries as deleted so they disappear from the UI, but maintains the ID to prevent syncing them again.
- **Hard Recovery (410 Gone)**: If the client's delta timestamp is outdated or expired on the server, the server returns an `HTTP 410 Gone` error. The sync engine catches this, performs a **Hard Reset** (wiping the local SQLite tables and purging `last_sync`), and automatically triggers a complete, clean, first-time full sync.

### 4. Clipboard Pinning Stack and Synchronization
To support pinning items to the top of the history list, Synclo implements a client-side pinning system that synchronizes in real time:
- **Ordering Algorithm**: Pinned clipboard entries are displayed at the top of the history list. They behave like a stack where the most recently pinned item is on top. This is implemented via the database query order:
  ```sql
  ORDER BY is_pinned DESC, pinned_at DESC, created_at DESC, ROWID DESC
  ```
- **Timestamp Separation**: While the sorting relies on the internal `pinned_at` timestamp (which is updated to `DateTime.UtcNow` upon pinning), the UI binding continues to display the item's original copy creation time (`CreatedAtLocal`), preserving the visual creation history.
- **Sync Interoperability**: When pinning is toggled locally, the local entry's `IsPinned` and `PinnedAt` properties are updated. The entry is marked as unsynced (`isSynced = 0`) and pushed over the WebSocket stream. When a remote pinning update is received, the sync engine checks if the incoming `is_pinned` status differs from the local database. If it does, the update is merged and UI reordered, bypassing the normal duplicate suppression that checks plaintext values.
- **UI Animation & Polish**:
  - **Card Interaction**: The explicit Copy button was removed, and clicking any part of a card copies its content to the operating system clipboard.
  - **Pin Toggling**: The card includes a Pin/Unpin icon button that toggles the pinned state without selecting the card (preventing accidental copying). All icons use default foreground coloring (`{DynamicResource Foreground}`).
  - **Staggered Clear Animation**: When clearing history, a bottom-up stagger animation is applied only to unpinned items. Once the animation loop reaches the index of a pinned item, the animation halts. All pinned items remain visually static, moving together smoothly into their final positions.

### 5. API Routing & WebSocket Protocol Versioning
To align with the versioned routes on the Synclo backend, the client utilizes structured, version-scoped communication paths:
- **REST Endpoints**: Formatted with the `/api/v1/` prefix (e.g., `/api/v1/register`, `/api/v1/login`, `/api/v1/devices`, `/api/v1/clipboard/sync`), constructed dynamically in [APIService.cs](file:///e:/Files/Code-Stuff/Projects/Synclo-Desktop/Features/Network_Services/APIService.cs).
- **WebSocket Route**: Connects securely to `/ws/v1/sync` in [WebSocketService.cs](file:///e:/Files/Code-Stuff/Projects/Synclo-Desktop/Features/Network_Services/WebSocketService.cs).
- **Device Lifecycle Sync Events**: The client captures real-time broadcasts pushed from the server WebSocket connection:
  - `device_added`: Raised when a new device is linked to the user account. Triggers `OnDeviceAdded` to dynamically refresh the active device cache.
  - `device_updated`: Raised when another active device updates its metadata (e.g., OS version during login). Triggers `OnDeviceUpdated`.
  - `device_deleted`: Raised when the current device is removed remotely by the user. Triggers `OnDeviceDeleted`, which safely terminates the local session and redirects to the login screen.

---

## 💾 Thread-Safe Database Design

Synclo uses **SQLite** (`Microsoft.Data.Sqlite`) for lightweight, offline-first persistence. Because SQLite locks the database file during simultaneous writes, concurrent operations in multi-threaded desktop applications can cause crashes and lockouts (`database is locked`).

Synclo resolves this natively using a **dedicated task queue scheduler**:

```csharp
private static readonly TaskScheduler _dbScheduler =
    new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default, 1).ExclusiveScheduler;

private static readonly TaskFactory _db =
    new(CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskContinuationOptions.None, _dbScheduler);
```

### Key Technical Aspects:
1. **Exclusive Execution**: The `ConcurrentExclusiveSchedulerPair` with a concurrency limit of `1` exposes an `ExclusiveScheduler`. Every task started via `_db.StartNew(...)` is scheduled sequentially on a single dedicated background thread.
2. **Elimination of Lock Statements**: Thread safety is achieved through scheduling rather than standard lock statements or semaphores, preventing UI thread blockages and database crashes.
3. **WAL (Write-Ahead Logging) Mode**: During database initialization, Synclo enforces WAL mode and sets appropriate concurrency pragmas:
   ```sql
   PRAGMA journal_mode=WAL;
   PRAGMA synchronous=NORMAL;
   PRAGMA temp_store=MEMORY;
   PRAGMA busy_timeout=5000;
   ```
   This configuration allows concurrent readers to query the database freely while the exclusive background thread processes write operations.
4. **Pinning Schema Extensions**: The `clipboard` table is extended to support pinning features with these columns:
   - `is_pinned`: `INTEGER NOT NULL DEFAULT 0` (Boolean flag to mark pinned status).
   - `pinned_at`: `TEXT` (ISO-8601 UTC timestamp of when it was pinned, allowing proper stack sorting).
   An index on `(is_pinned, pinned_at)` is established during initialization to optimize search, ordering, and synchronization processes.
5. **Partial Clear Mechanism**: Instead of deleting all history entries, the clearing method only targets unpinned rows (`is_pinned = 0`), marking unpinned entries as deleted and maintaining the records as tombstones (`is_deleted = 1`) to preserve sync synchronization logic, while leaving pinned records completely untouched.

---

## 🖥️ Platform-Native Integrations

Rather than relying on third-party cross-platform frameworks which introduce performance bloat, Synclo leverages **P/Invoke (Platform Invoke)** into native OS dynamic libraries (`.dll`, `.dylib`, `.so`) to access host services.

### 1. Platform-Native Clipboard Hooking

Clipboard monitoring is governed by the [IClipboardMonitor](file:///e:/Files/Code-Stuff/Projects/Synclo-Desktop/Features/Clipboard_Manager/Clipboard_Monitor/IClipboardMonitor.cs) interface. The correct platform-specific implementation is resolved at runtime using the static `ClipboardMonitorFactory`:

- **Windows (`ClipboardMonitorWindows.cs`)**:
  - Registers a Win32 message-only window using `RegisterClassEx` and `CreateWindowEx` targeting the special parent handle `HWND_MESSAGE` (`-3`).
  - Implements a custom `WndProc` delegate that listens for the `WM_CLIPBOARDUPDATE` (`0x031D`) message.
  - Registers the window handle using `AddClipboardFormatListener(hwnd)`. When the OS clipboard changes, the thread receives `WM_CLIPBOARDUPDATE`, which triggers clipboard reading without polling.
  - Utilizes native `OpenClipboard`, `GetClipboardData` with `CF_UNICODETEXT` (`13`), and `GlobalLock` to read text efficiently.
- **macOS / Linux**:
  - Leverages robust polling services that periodically inspect clipboard hashes, utilizing native APIs when available.

---

### 2. Platform-Native Secure Storage

Synclo stores sensitive session tokens, Argon2id salts, and wrapped/unwrapped master encryption keys in platform-specific secure storage layers. The appropriate storage engine is resolved at runtime via the static `SecretsManagerFactory`:

#### Windows (`SecureStorageWindows.cs`)
Invokes native Windows Credential Manager generic password APIs via `advapi32.dll`:
- **`CredWriteW`**: Saves credentials formatted with a `Synclo:` prefix. Scoped with `CRED_PERSIST_ENTERPRISE` (`3`) for user-profile persistence.
- **`CredReadW`**: Retrieves credentials.
- **`CredDeleteW`**: Erases generic credentials.
- **Thread Safety**: All operations are offloaded to background threads using `Task.Run` and throttled using an internal `SemaphoreSlim` lock.

#### macOS (`SecureStorageMacOS.cs`)
Integrates directly with the **macOS Keychain** using Apple's Security Framework.
- **Dynamic Loading**: Dynamically loads `/System/Library/Frameworks/Security.framework/Security` and `CoreFoundation.framework` at runtime using `dlopen`, `dlsym`, and `dlclose` from `libSystem.dylib`.
- **APIs Invoked**:
  - `SecItemAdd`: Inserts new generic password entries (`kSecClassGenericPassword`).
  - `SecItemCopyMatching`: Queries and reads Keychain items.
  - `SecItemUpdate`: Updates existing entries when a key rotations occur.
  - `SecItemDelete`: Deletes entries.
- **Scoping**: Attributes are created with `kSecAttrAccessibleAfterFirstUnlock` to allow background synchronization to fetch keys once the user logs in.

#### Linux (`SecureStorageLinux.cs`)
A hybrid approach utilizing system keyring integration with an automated encrypted local backup:
- **Primary Integration**: Connects via **Tmds.DBus** to the freedesktop Secret Service (`org.freedesktop.secrets` service at `/org/freedesktop/secrets/collection/login`). It opens a session via dynamic Unix Domain sockets and executes `CreateItemAsync` / `SearchItemsAsync`.
- **Encrypted Fallback**: If a DBus bus is absent (e.g., headless environments, minimal window managers), it automatically falls back to an encrypted JSON file (`~/.config/Synclo/linux_secrets.json`).
  - **Machine Key**: A machine-specific 256-bit encryption key is derived using `Pbkdf2` from the machine id (read from `/etc/machine-id`) combined with the local username.
  - **Encryption**: Values are encrypted using `AesGcm` (12-byte random nonce, 16-byte authentication tag) before being saved to the JSON file. A cross-process file-lock ensures durability.

---

### 3. Native Startup Registration

Managed by [IStartupManager](file:///e:/Files/Code-Stuff/Projects/Synclo-Desktop/Features/Startup_Manager/IStartupManager.cs) and resolved at runtime via the static `StartupManagerFactory`:

- **Windows (`StartupManagerWindows.cs`)**:
  - Adds a string value named `Synclo` containing `"{ExecutablePath}" --autostart` inside the registry key `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run`.
- **macOS (`StartupManagerMacOS.cs`)**:
  - Creates a standard plist launch agent inside the user's directory: `~/Library/LaunchAgents/com.synclo.app.plist`. The plist is configured to execute the application binary on startup with the `--autostart` flag.
- **Linux (`StartupManagerLinux.cs`)**:
  - Standardizes the autostart protocol by creating a `.desktop` file at `~/.config/autostart/synclo.desktop`:
    ```ini
    [Desktop Entry]
    Type=Application
    Name=Synclo
    Exec={ExecutablePath} --autostart
    Hidden=false
    NoDisplay=false
    X-GNOME-Autostart-enabled=true
    ```

---

## ⚙️ Application Settings Configuration

Local client configurations are parsed from and serialized to a local `appsettings.json` file. This layout is managed by the [SettingsService](file:///e:/Files/Code-Stuff/Projects/Synclo-Desktop/Features/Settings_Manager/SettingsService.cs) using a strongly typed [AppSettings](file:///e:/Files/Code-Stuff/Projects/Synclo-Desktop/Models/AppSettings.cs) model.

Key parameters include:
- **`ServerUrl`**: The base HTTP/HTTPS URL of the target Synclo backend server. Custom URLs are normalized and validated for reachability before saving. To protect privacy, custom server URLs are stored in secure credentials stores using platform-native APIs.
- **`Theme`**: Visual layout options (`Light`, `Dark`, `System`). Resolves automatically at startup.
- **`sync_page_size`**: Universal page chunk size for pagination requests during delta synchronization checks (Default: `100`).
- **`minimize_to_tray`**: Intercepts close button triggers on the window, minimizing the application to the tray rather than terminating the process (Default: `true`).
- **`background_sync_enabled`**: Allows synchronization channels and WebSocket pipelines to remain operational even when the visual window is closed or hidden (Default: `true`).
- **`silent_boot`**: Sets whether the application should bypass visual rendering and boot quietly to the system tray when initiated with the `--autostart` flag (Default: `false`).

---

## 🔒 Cryptographic Design

Synclo's cryptographic architecture is built on the [CryptographyService](file:///e:/Files/Code-Stuff/Projects/Synclo-Desktop/Utilities/CryptographyService.cs):


### Cryptographic Protocol & Lifecycle Details

Synclo's security architecture relies on a strict **Zero-Knowledge** authenticated envelope-encryption paradigm. The cryptographic lifecycle is divided into three distinct pipeline phases:

#### Phase 1: Password Key Derivation & Separation (KDF)
When a user logs in or registers, their master password is never stored or transmitted in plaintext.
1. **Base Key Derivation**: The password and a cryptographically secure random salt are passed to the **Argon2id** key derivation function. This derives a 32-byte (256-bit) computationally hard `Base Key`. Using Argon2id protects against hardware-accelerated dictionary attacks (GPU/ASIC cracking).
2. **Key Separation via HKDF-SHA256**: To prevent key reuse vulnerabilities (using the same key for multiple cryptographic roles), the derived `Base Key` is split into two cryptographically independent keys using the **HKDF-SHA256** standard:
   - **Authentication Key (`authKey`)**: Derived using the context label `auth_key|kdf_v1`. This is the only key sent to the server for authentication (login and registration checks). Because HKDF is a strong one-way function, the server can never reverse the `authKey` to obtain the base key, wrapping key, or user password.
   - **Wrapping Key (`wrappingKey`)**: Derived using the context label `wrapping_key|kdf_v1`. This key is kept strictly client-side and is never shared, transmitted, or backed up. Its sole purpose is to wrap and secure the Master Key.

#### Phase 2: Master Key Generation & Secure Key Wrapping
All clipboard content is encrypted client-side using a locally generated **Master Key** (a cryptographically secure random 256-bit value generated via `RandomNumberGenerator`).
1. **Envelope Encryption (Key Wrapping)**: To allow synchronization across multiple devices, the local client wraps the Master Key using **AES-256-GCM** authenticated encryption with the `wrappingKey` as the keying material. 
2. **Wrapping Metadata**: The wrapping operation uses a cryptographically secure 12-byte random nonce, a 16-byte authentication tag, and Associated Additional Data (AAD) set to `wrap_mk_v1`.
3. **Backup Transmission**: The resulting wrapped Master Key payload (encrypted ciphertext, nonce, and tag) is backed up to the server.
4. **Multi-Device Sync**: When a secondary device logs in with the user's password, it derives the identical `wrappingKey`, downloads the wrapped master key from the server, and decrypts (unwraps) it locally.
5. **Memory Protection**: Once unwrapped, the Master Key resides in RAM and is cached securely. During logout or key rotation, all raw byte arrays containing the password, base keys, wrapping keys, or master keys are immediately wiped from memory using `CryptographicOperations.ZeroMemory()`.

#### Phase 3: Client-Side Clipboard Payload Sync
Every clipboard synchronizing operation is encrypted client-side using the local **Master Key**.
1. **Encryption**: When a clipboard event is captured, the plaintext is encrypted using **AES-256-GCM** with the **Master Key**:
   - **Nonce**: A unique 12-byte cryptographically secure random value generated for each copy event. Nonce reuse in GCM is catastrophic, so Synclo enforces strict random generation for every transaction.
   - **Tag**: A 16-byte authentication tag verifying ciphertext integrity.
   - **AAD**: Set to `clipboard_v1` to bind the payload's context, preventing ciphertext-substitution or replay attacks.
2. **Transmission**: The output package `{ entry_id, ciphertext, nonce, tag }` (where all byte blobs are Base64 encoded) is stored in SQLite and dispatched over the WebSocket connection.
3. **Decryption**: Connected client devices receive the payload, load the Master Key from their platform-native secure storage, and decrypt the payload to write the validated plaintext back to their local clipboard.

---

### Algorithm Technical Specifications:
- **Argon2id Parameters**:
  - `Iterations (TimeCost)`: `2`
  - `Memory (MemoryCost)`: `65536 KB` (64 MB)
  - `DegreeOfParallelism`: `1`
  - Output Key Length: `32 bytes` (256 bits)
- **HKDF**:
  - Salt label applied: `hkdf_salt_v1`
  - Info labels applied: `auth_key|kdf_v1` (for authentication key) and `wrapping_key|kdf_v1` (for key wrapping key).
- **AES-GCM (Authenticated Encryption)**:
  - Both key wrapping and clipboard payload encryption use **AES-256-GCM** with a **16-byte authentication tag** (`AesGcm.TagByteSizes.MaxSize`).
  - GCM Nonces are strictly **12 bytes** generated using `RandomNumberGenerator.GetBytes`.
  - Associated Additional Data (AAD) applied:
    - Key wrapping AAD: `wrap_mk_v1`
    - Clipboard payload AAD: `clipboard_v1`

---

## 🏁 Conclusion

Synclo-Desktop's architecture represents a premium, high-integrity desktop application built on modern, secure C# and Avalonia UI conventions. By isolating SQLite database tasks to a dedicated, exclusive background thread scheduler, integrating platform-native system hooks via low-overhead dynamic P/Invokes, utilizing memory-bounded producer-consumer channels for robust real-time synchronization, and securing all assets under a Zero-Knowledge AES-GCM-256 envelope KDF encryption standard, Synclo achieves enterprise-grade security and top-tier client-side desktop performance.

Developers maintaining or expanding this codebase should refer to the accompanying [CONTRIBUTING.md](CONTRIBUTING.md) and [AGENTS.md](AGENTS.md) files to ensure code adjustments respect these native platform patterns and structural constraints.

