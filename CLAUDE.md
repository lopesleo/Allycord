# CLAUDE.md

Xcord: Xbox Game Bar widget with Discord voice controls for the ROG Xbox Ally X. Port of
`D:\dev\Discord-Lite` (Decky plugin). Architecture and roadmap: `PLAN.md`.

## Layout

- `src/Xcord.Bridge` — .NET 9 full-trust exe (`net9.0-windows10.0.19041.0`). Discord RPC over the
  named pipe (`Discord/IpcClient.cs`), OAuth PKCE + refresh (`Discord/OAuth.cs`), voice state and
  command handling (`Discord/DiscordSession.cs`), AppService client to the widget (`WidgetHost.cs`).
- `src/Xcord.Widget` — classic UWP C# (.NET Native in Release), Game Bar SDK
  `Microsoft.Gaming.XboxGameBar`. `MainPage` renders the bridge's state snapshot; `BridgeClient`
  hosts the in-proc AppService `XcordBridge` and launches the bridge via `FullTrustProcessLauncher`.
  Classic UWP: no records/init-only setters (no runtime support), use `Windows.Data.Json`.

## Build / run

```powershell
.\build.ps1            # publish bridge into src\Xcord.Widget\Bridge, build widget, register (Debug)
.\build.ps1 -Release   # signed package + dist\Xcord-<ver>-x64.zip for a GitHub release
dotnet build src\Xcord.Bridge; src\Xcord.Bridge\bin\Debug\...\Xcord.Bridge.exe --console   # bridge alone
```

MSBuild must be the VS one with the UWP workload (`dotnet build` can't build the widget).

## Gotchas

- No built-in Discord Client ID: each user brings their own app (RPC private beta). It lives in
  `LocalState\settings.json` (`AppConfig`); status `needs_setup` drives the widget's setup screen.
  Error codes the widget translates: `invalid_client_id`, `not_tester`, `not_public_client`.

- Package manifest proxy-stub list comes from the Game Bar NuGet readme, which has a wrong IID for
  `IXboxGameBarWidgetNotificationHost` (fixed here from the winmd). Recheck IIDs when bumping the SDK.
- IPC events arrive on the read loop thread: never await a Discord response from inside
  `DiscordSession.OnEvent` (deadlock) — offload with `Task.Run`.
- Re-registering keeps LocalState (token); `Remove-AppxPackage` wipes it.
- Bridge exits when the AppService closes (widget suspended/closed); the widget relaunches it.
