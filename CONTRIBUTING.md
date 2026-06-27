# Contributing to Synclo-Desktop

Thank you for your interest in contributing to **Synclo**! As a cross-platform desktop application utilizing compiled bindings, native dynamic-link library bindings (P/Invoke), and local SQLite thread scheduling, keeping the codebase optimized, reliable, and clean is paramount.

This guide outlines our setup processes, coding standards, structural requirements, and commit guidelines.

---

## 🛠️ Development Environment Setup

### Prerequisites

1. **.NET 10 SDK**: The project strictly targets `net10.0`. Ensure you have the latest .NET 10 SDK installed.
2. **IDE Selection**:
   - **JetBrains Rider** (Highly Recommended): Excellent Avalonia XAML previewer and refactoring toolsets.
   - **Visual Studio 2022** (v17.10+): Install the "Avalonia XAML Extension" from the Extensions Marketplace.
   - **VS Code**: Install the ".NET MAUI" and C# Dev Kit extensions.
3. **OS-Specific CLI Requirements (for testing Linux/macOS integrations)**:
   - **Linux**: Make sure `dbus` and `libsecret-1-dev` (or standard desktop system keyrings) are running.

### Getting the Code

1. **Fork the Repository** on GitHub.
2. **Clone your fork** locally:
   ```bash
   git clone https://github.com/your-username/Synclo-Desktop.git
   cd Synclo-Desktop
   ```
3. **Restore Packages**:
   ```bash
   dotnet restore
   ```
4. **Compile and Run**:
   ```bash
   dotnet build
   dotnet run
   ```

---

## 📝 Coding Standards & Conventions

To maintain codebase health and performance across Windows, macOS, and Linux, all contributions must adhere to the following rules:

### 1. Nullable Reference Types & Safety
- Nullable checking is explicitly enabled (`<Nullable>enable</Nullable>`).
- Do not bypass warnings using the null-forgiving operator (`!`) unless you can mathematically prove that the value cannot be null at runtime.
- Always use nullable annotations for parameters, return values, and properties where data may be absent.

### 2. Compiled XAML Bindings
- Compiled bindings are enabled globally (`<AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>`).
- All binding expressions in XAML are checked at compile-time, eliminating slow reflection lookups at runtime.
- **XAML Requirement**: Every `Window`, `UserControl`, or visual node containing bindings **MUST** specify its datacontext type using the `x:DataType` attribute:
  ```xml
  <UserControl xmlns="https://github.com/avaloniaui"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
               xmlns:vm="using:Synclo.ViewModels"
               x:Class="Synclo.Views.HomeView"
               x:DataType="vm:HomeViewModel">
      <!-- Your layout -->
      <TextBlock Text="{Binding StatusText}" />
  </UserControl>
  ```

### 3. Cross-Platform Coding & Directory Scoping
- **Zero Raw OS Calls**: Do not execute direct Windows Win32 API calls, macOS Carbon/Cocoa APIs, or Linux system calls without wrapping them in appropriate runtime operating system checks:
  ```csharp
  if (OperatingSystem.IsWindows())
  {
      // Safe Windows-only native P/Invoke
  }
  else if (OperatingSystem.IsMacOS())
  {
      // Safe macOS-only dynamic loading
  }
  ```
- **Generic File Paths**: Never hardcode file-path separators (`\`, `/`). Always use `System.IO.Path.Combine()` and OS-relative folders via `Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)`.
- **Loose Coupling**: Register and inject all platform-specific classes behind interfaces (`ISecureStorage`, `IStartupManager`, `IClipboardMonitor`) inside [DependencyInjection.cs](file:///e:/Files/Code-Stuff/Projects/Synclo-Desktop/Utilities/DependencyInjection.cs).

### 4. UI & Styling Rules
- Avoid styling elements inline. Use shared resources or dynamic theme parameters located under the `Themes/` directory.
- Avoid defining UI status colors directly inside ViewModels. Use converters (e.g., [OSToIconConverter.cs](file:///e:/Files/Code-Stuff/Projects/Synclo-Desktop/Converters/OSToIconConverter.cs), [ClipboardItemTypeToIconConverter.cs](file:///e:/Files/Code-Stuff/Projects/Synclo-Desktop/Converters/ClipboardItemTypeToIconConverter.cs), or [PinConverters.cs](file:///e:/Files/Code-Stuff/Projects/Synclo-Desktop/Converters/PinConverters.cs)) inside the XAML View layer to resolve visual representations.
- All action buttons and icons must respect theme definitions (e.g., using `Foreground="{DynamicResource Foreground}"`) to ensure seamless readability across dark and light visual modes.
- Register all reusable value converters as application resources inside [App.axaml](file:///e:/Files/Code-Stuff/Projects/Synclo-Desktop/App.axaml).

---

## 🪵 Diagnostics & Local Debugging

### Locating the SQLite Database
During runtime development, local clipboard synchronizations are written to a SQLite file. You can open and inspect this database with tools like **DB Browser for SQLite**:
- **Windows**: `%APPDATA%\Synclo\clipboard.db`
- **Linux**: `~/.config/Synclo/clipboard.db`
- **macOS**: `~/Library/Application Support/Synclo/clipboard.db`

### Accessing Debug Logs
Synclo wires up standard .NET logging providers.
- When running the application through an IDE or command line (`dotnet run`), logs are redirected to the standard Console/Output streams.
- To inspect WebSocket transactions, look for logger channels prefixed with `Synclo.Services.API.WebSocketService`.

---

## 🍴 Git Workflow & Pull Requests

### Branch Naming Conventions
Create descriptive branch names scoped by task type:
- `feature/your-feature-name` (for new features)
- `bugfix/issue-description` (for bug fixes)
- `docs/what-changed` (for documentation updates)

### Commit Guidelines
We recommend adopting the **Conventional Commits** standard:
- `feat: added native clipboard monitor for macOS`
- `fix: resolved SQLite database lock during concurrent delta sync`
- `docs: updated contribution standards`
- `chore: upgraded Avalonia nuget packages`

### Pull Request Checklist
Before opening a pull request, verify that:
1. The project compiles successfully without errors or compile-time warnings:
   ```bash
   dotnet build --configuration Release
   ```
2. Your changes do not violate nullable checks or compile-time binding requirements.
3. Native P/Invoke functions handle execution failure cases gracefully and do not cause crashes on unsupported operating systems.
4. Your branch is fully rebased onto the upstream `main` branch.
