# CLAUDE.md

Allycord: Xbox Game Bar widget with Discord voice controls for the ROG Xbox Ally X. Port of
`D:\dev\Discord-Lite` (Decky plugin). Architecture and roadmap: `PLAN.md`.

## Layout

- `src/Allycord.Bridge` — .NET 9 full-trust exe (`net9.0-windows10.0.19041.0`). Discord RPC over the
  named pipe (`Discord/IpcClient.cs`), OAuth PKCE + refresh (`Discord/OAuth.cs`), voice state and
  command handling (`Discord/DiscordSession.cs`), AppService client to the widget (`WidgetHost.cs`).
- `src/Allycord.Widget` — classic UWP C# (.NET Native in Release), Game Bar SDK
  `Microsoft.Gaming.XboxGameBar`. `MainPage` renders the bridge's state snapshot; `BridgeClient`
  hosts the in-proc AppService `AllycordBridge` and launches the bridge via `FullTrustProcessLauncher`.
  Classic UWP: no records/init-only setters (no runtime support), use `Windows.Data.Json`.

## Build / run

```powershell
.\build.ps1            # publish bridge into src\Allycord.Widget\Bridge, build widget, register (Debug)
.\build.ps1 -Release   # signed package + dist\Allycord-<ver>-x64.zip for a GitHub release
dotnet build src\Allycord.Bridge; src\Allycord.Bridge\bin\Debug\...\Allycord.Bridge.exe --console   # bridge alone
```

MSBuild must be the VS one with the UWP workload (`dotnet build` can't build the widget).

## Gotchas

- **Never marshal `Vector3` to WinRT from the widget** (`UIElement.Translation`/`Scale`, x:Bind to them):
  CoreCLR on UWP dies with `ExecutionEngineException` in `IL_STUB_CLRtoWinRT(Vector3)`. Opacity-only motion.
- **Crashes: read the dump first.** Full dumps land in `%LOCALAPPDATA%\CrashDumps\Allycord.Widget.exe.*.dmp`;
  read managed stacks with ClrMD + the DAC at `Microsoft.NET.CoreRuntime.2.2_*\mscordaccore.dll`.
- **Performance is a requirement** (ROG Ally): no looping animations, decode images at display size,
  lazy-create rarely used views (`x:Load`), measure CPU/RAM of the Release build before claiming wins.
- **Demo mode** for design review/screenshots: `LocalState\demo.txt` with a scene (`call`, `idle`, `channels`,
  `channels+open`, `channels+search`, `audio`, `setup`, `auth`, `connecting`, `nodiscord`, `pinned`), then
  launch from Start. Demo data must stay fictional (no real users/servers/Client IDs: screenshots are public).
- One bridge per session (named mutex + `BridgeClient.EnsureStartedAsync` shares one launch).
- Same-version re-register from another folder can "succeed" without switching; `build.ps1` checks
  `InstallLocation` and reinstalls (carrying `settings.json`/`discord_token.json` over).
- Icons come from `tools\make-logo.ps1` (wide handheld mark for large assets, compact tile ≤ 64px).
- No built-in Discord Client ID: each user brings their own app (RPC private beta). It lives in
  `LocalState\settings.json` (`AppConfig`); status `needs_setup` drives the widget's setup screen.
  Error codes the widget translates: `invalid_client_id`, `not_tester`, `not_public_client`.

- Package manifest proxy-stub list comes from the Game Bar NuGet readme, which has a wrong IID for
  `IXboxGameBarWidgetNotificationHost` (fixed here from the winmd). Recheck IIDs when bumping the SDK.
- IPC events arrive on the read loop thread: never await a Discord response from inside
  `DiscordSession.OnEvent` (deadlock) — offload with `Task.Run`.
- Re-registering keeps LocalState (token); `Remove-AppxPackage` wipes it.
- Bridge exits when the AppService closes (widget suspended/closed); the widget relaunches it.
