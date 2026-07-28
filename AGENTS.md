# Repository Guidelines

## Project Structure & Module Organization

The repository root contains localized documentation (`README.md`, `README_EN.md`, `README_JA.md`, and `README_KO.md`) and the UI screenshot `example.png`. Plugin code lives in `NintendoProBridge/`:

- `Plugin.cs`: Dalamud entry point, configuration, ImGui settings, and localization.
- `NativeHid.cs`: Windows HID discovery and device opening.
- `ProControllerInput.cs`: report decoding, FFXIV hooks, calibration, and input translation.
- `SwitchRumble.cs`: Nintendo rumble packet encoding.
- `NintendoProBridge.json`: plugin manifest; keep its version aligned with the project file.

There is currently no automated test project.

## Build, Test, and Development Commands

Run commands from the repository root:

```powershell
dotnet build .\NintendoProBridge\NintendoProBridge.csproj -c Release
git diff --check
```

The build requires the Dalamud development assemblies under `%APPDATA%\XIVLauncher\addon\Hooks\dev`. Release output is written to `NintendoProBridge/bin/Release/net10.0-windows/`. Load the DLL through Dalamud's local plugin loader and use `/npro` to open the settings window.

## Coding Style & Naming Conventions

Use C# with four-space indentation, nullable reference types, and file-scoped namespaces. Follow standard .NET naming: `PascalCase` for types and public members, `camelCase` for locals and private fields, and descriptive async method names ending in `Async`. Keep HID parsing bounds-checked and keep hook callbacks exception-safe. Avoid unrelated formatting changes.

When adding UI text, update every language entry in `LocalizedText`. Keep `NintendoProBridge.csproj` and `NintendoProBridge.json` versions identical.

## Testing Guidelines

Every change must compile with zero warnings. Manually verify connection, `/npro` toggling, buttons, both sticks, calibration, disconnect/reconnect, and rumble where affected. Test USB and Bluetooth separately. A device appearing in the selector does not prove its report protocol is supported.

## Commit & Pull Request Guidelines

Recent commits use short prefixes such as `feat:`, `fix:`, and `release:`. Keep commits focused and imperative, for example `fix: handle HID disconnect during refresh`. Pull requests should explain behavior changes, list manual test results, and include screenshots for UI changes. Call out manifest or version changes explicitly.

## Safety & Architecture Constraints

The plugin must operate only inside FFXIV. Do not add HidHide, virtual controllers, drivers, administrator requirements, or system-wide input changes. Preserve user configuration compatibility and avoid assuming that all Nintendo VID `0x057E` devices share the Pro Controller report format.
