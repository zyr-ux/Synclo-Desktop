# Synclo-Desktop AI Agent Handbook (AGENTS.md)

Welcome, AI Developer Agent! This document is designed specifically for you. It provides a high-density, highly technical onboarding reference of **Synclo-Desktop**'s architecture, design constraints, and hidden gotchas. 

Read this file before making any code modifications, refactorings, or introducing new features to ensure you preserve Synclo's performance, security, and cross-platform integrity.

---

## 🧭 System Snapshot & Architecture Blueprint

Synclo-Desktop is an offline-first, end-to-end encrypted clipboard synchronization utility. When initialized, it operates as follows:
1. **Capturing (Local Copy)**: Platform-native [IClipboardMonitor](file:///e:/Files/Code-Stuff/Projects/Synclo-Desktop/Features/Clipboard_Manager/Clipboard_Monitor/IClipboardMonitor.cs) detects an OS-level copy event.
2. **Encryption (Client-Side)**: Plaintext is encrypted with **AES-256-GCM** using the derived local 32-byte master key inside [CryptographyService](file:///e:/Files/Code-Stuff/Projects/Synclo-Desktop/Utilities/CryptographyService.cs).
3. **Persistence (SQLite queue)**: The encrypted payload (with unique ID, tag, and nonce) is written to local SQLite via [ClipboardRepository](file:///e:/Files/Code-Stuff/Projects/Synclo-Desktop/Features/Clipboard_Manager/Clipboard_Service/ClipboardRepository.cs) scheduling on an exclusive single-thread.
4. **Transmission (WS / REST)**: The client pushes the payload to the server over a persistent WebSocket [WebSocketService](file:///e:/Files/Code-Stuff/Projects/Synclo-Desktop/Features/Network_Services/WebSocketService.cs) and awaits a server ACK.
5. **Propagation (Remote Write)**: Synced devices receive the WebSocket payload, decrypt it client-side, persist it to SQLite, temporarily disable the local copy monitor using a **Suppression Guard**, and write the plaintext to the OS clipboard.
6. **Pinning Synchronization**: Toggling the pin state updates the local DB entry, sets `isSynced = 0`, and broadcasts the update frame over WebSocket. Incoming remote updates containing pin status changes are processed and merged without skipping, ensuring the pinning state is synchronized across all devices.

---

## ⚠️ Strict Architectural Constraints

To keep the application stable, you must strictly follow these structural constraints:

### 1. Interface-Based Dependency Injection (DI)
- **Rule**: All services, managers, and viewmodels must be registered in [DependencyInjection.cs](file:///e:/Files/Code-Stuff/Projects/Synclo-Desktop/Utilities/DependencyInjection.cs).
- **Rule**: Never register a service directly as a concrete class if it implements an interface. Always register the service behind its interface (e.g. `services.AddSingleton<IClipboardRepository, ClipboardRepository>()`).
- **Reason**: Breaks loose coupling, compromises mockability, and violates C# unit testing standards.

### 2. Stateless Cryptographic Infrastructure
- **Rule**: [CryptographyService](file:///e:/Files/Code-Stuff/Projects/Synclo-Desktop/Utilities/CryptographyService.cs) must remain **stateless**. Do not add static properties or static global variables storing decrypted master keys or passwords.
- **Rule**: Master keys must be derived dynamically or retrieved via secure DI parameters, and wiped from memory using `CryptographicOperations.ZeroMemory()` as soon as the AES-GCM operation completes.
- **Reason**: Prevents heap leakage of high-value decryption keys, ensuring Zero-Knowledge confidentiality.

### 3. Database Single-Thread Concurrency Queue
- **Rule**: Do not perform raw SQLite transactions from background threads outside of [ClipboardRepository](file:///e:/Files/Code-Stuff/Projects/Synclo-Desktop/Features/Clipboard_Manager/Clipboard_Service/ClipboardRepository.cs).
- **Rule**: All repository operations must use the exclusive single-threaded task scheduler:
  ```csharp
  private static readonly TaskScheduler _dbScheduler =
      new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default, 1).ExclusiveScheduler;
  ```
- **Reason**: SQLite throws generic `database is locked` exceptions if multiple threads attempt simultaneous writes. Restricting operations to a single background queue prevents data race conditions and concurrency crashes.

### 4. Remote Feedback Loop Suppression Guard
- **Rule**: When writing an incoming remote clipboard item to the local OS clipboard, you **MUST** wrap the write sequence inside the volatile suppression guard:
  ```csharp
  _isProcessingRemoteUpdate = true;
  try
  {
      await _monitor.SetClipboardTextAsync(remoteText);
  }
  finally
  {
      _isProcessingRemoteUpdate = false;
  }
  ```
- **Reason**: Writing to the clipboard triggers the OS clipboard update message window. Without setting `_isProcessingRemoteUpdate` to `true`, the local listener will detect this as a new "local copy" and re-sync it back to the server, creating a continuous feedback loop that exhausts system resources.

### 5. UI Thread Affinity & Dispatcher Scoping
- **Rule**: Any background sync operations, WebSocket listeners, or API callbacks that attempt to modify visual controls, raise ViewModel property-changed notifications, or show OS dialogs **MUST** be explicitly pushed to the UI thread using Avalonia's Dispatcher:
  ```csharp
  await Dispatcher.UIThread.InvokeAsync(() => 
  {
      // Safe visual changes / notifications
  });
  ```
- **Reason**: Desktop frameworks restrict UI object updates to the main thread (STA thread). Modifying views or bindings from background threads results in immediate runtime crashes.

### 6. Strict Prohibition of Automated Git Commits
- **Rule**: AI Developer Agents must **never** automatically run `git commit`, `git push`, or any other automated version control transaction on behalf of the user. 
- **Rule**: All file edits, refactorings, and additions must remain in the working directory as unstaged/staged modifications. The final review, staging, and committing sequence is reserved **exclusively** for the human developer.
- **Reason**: Maintains manual developer control over the git history, signature signing, branch staging, and repository integrity.

### 7. Clipboard Pinning and Ordering Rules
- **Rule**: All database select queries fetching clipboard entries must enforce the strict pinning stack order: `ORDER BY is_pinned DESC, pinned_at DESC, created_at DESC, ROWID DESC`.
- **Rule**: When synchronizing updates from WebSocket/API, never skip processing an incoming frame solely because the plaintext content matches an existing local entry. Verify if the pin state (`is_pinned`) has changed and apply the update.
- **Rule**: The UI must display the original copy creation timestamp (`CreatedAtLocal`), even though the order of pinned items is governed by `pinned_at`.
- **Rule**: Historical clear operations must only clear unpinned items (`ClearUnpinnedAsync()`), preserving pinned items both locally and in synchronization requests.
- **Rule**: Clear animations in the UI must stagger-animate unpinned items from the bottom-up and halt immediately when a pinned item is encountered, leaving pinned items static.
- **Reason**: Maintains ordering consistency, ensures remote pin updates propagate, avoids unwanted data loss of pinned history, and prevents visual jarring.

### 8. Separation of Service Logic from UI Notifications
- **Rule**: Service layers (such as `ClipboardSyncService` and `AccountService`) must never directly resolve or invoke the visual `INotificationService` to show user-facing messages or alerts.
- **Rule**: If a service layer method is user-initiated (e.g. `DeleteClipboardEntryAsync`, `TogglePinClipboardEntryAsync`, `ClearHistoryAsync`), any exceptions must bubble up to the ViewModel layer to be caught and reported.
- **Rule**: If an event occurs asynchronously in a background worker (e.g. remote logouts, WebSocket status errors, local processing errors), the service should raise an event (`OnSyncError`, `OnLoggedOutRemotely`) that UI-bound managers (like `HomeViewModel` or `App.axaml.cs`) listen to and marshal onto the UI thread for presentation.
- **Reason**: Decouples business logic from presentation, prevents duplicated notifications, allows headless testing, and protects against background-thread visual marshaling errors.

---

## 🪵 Known Engineering Gotchas

When modifying code in the synchronization layers, watch out for these recurring bugs:

1. **Dual Notifications**: Ensure that adding an item to the local SQLite database does not trigger a local system tray notification if the item was *locally copied* by the user. Notifications should only fire for incoming *remote* items.
2. **Delta Sync Expiration (410 Gone)**: If the client's `last_sync` timestamp is out of sync with the server database, the server returns `HTTP 410 Gone`. The catch block **MUST** perform a hard reset (calling `ClearAllAsync()`, resetting `last_sync = null`, and triggering a recursive `SyncInBackgroundAsync()`) to recover gracefully.
3. **Graceful Shutdown Wait**: During application exit, the channel consumer must wait for pending database writes to complete. Ensure you cancel the `CancellationTokenSource _shutdownCts` and await the `ProcessClipboardChannelAsync` task with a reasonable timeout.
4. **Windows generic credential size limits**: Windows Generic Vault (`CRED_TYPE_GENERIC`) has a hard limit of **512 bytes** for credential blobs. Do not store massive JSON structures or tokens inside `SecureStorageWindows.cs`.
5. **WebSocket Pin Merging**: When updating the pin status of an entry from the network, ensure the local DB is updated with `is_pinned` and `pinned_at` (mapped from the server's update timestamp or sync payload) to avoid infinite sync cycles or local UI ordering mismatch.
6. **Card Click vs Pin Toggle Event Bubble**: Ensure the Pin button click doesn't trigger card selection, or that the Pin command is isolated so that toggling a pin doesn't cause the card's content to be copied to the clipboard.

---

## 📋 Code Modifier Checklist for AI Agents

Before submitting any code modifications, verify your implementation against this checklist:

- [ ] **Platform Checks**: Are native API operations wrapped in platform-scoping checks (`OperatingSystem.IsWindows()`, etc.)?
- [ ] **DI Integrity**: Did you register any new service behind its interface in `DependencyInjection.cs`?
- [ ] **Thread Scoping**: Do your SQLite modifications run inside the custom single-thread `_dbScheduler` queue?
- [ ] **UI Safety**: Are any visual property modifications routed through `Dispatcher.UIThread.InvokeAsync`?
- [ ] **Memory Protection**: Did you call `CryptographicOperations.ZeroMemory()` on raw byte arrays containing passwords or keys?
- [ ] **Feedback Loop Prevention**: Does your clipboard setting code check and toggle the `_isProcessingRemoteUpdate` guard?
- [ ] **Graceful Disposals**: If you created disposable class connections, are they cleaned up in the parent's `Dispose()` or `ShutdownAsync()`?
- [ ] **No Automated Git Commits**: Did you refrain from staging/committing the code automatically, leaving it entirely for human developer review?
- [ ] **Pin Sorting**: Do SQLite queries select and order clipboard entries via `is_pinned DESC, pinned_at DESC, created_at DESC, ROWID DESC`?
- [ ] **Pin Sync Safety**: Did you ensure that incoming sync events verify and merge `is_pinned` updates even if the content matches?
- [ ] **History Clear Behavior**: Does `ClearHistoryAsync` call `ClearUnpinnedAsync` to keep pinned items intact?
- [ ] **Tray & UI Icon Colors**: Do UI buttons and icons use `{DynamicResource Foreground}` to respect theme styling instead of static colors?
- [ ] **Dynamic Typography**: Do text elements, controls, and XAML styles use `FontFamily="{DynamicResource AppFontFamily}"` rather than static font resources to allow live font switching?
- [ ] **Notification Separation**: Did you avoid calling `INotificationService` directly inside service layer files, propagating errors via events or exceptions instead?

