# Xcord

**Discord voice controls inside Xbox Game Bar** — built for the **ROG Xbox Ally / Ally X** and any Windows handheld or PC.

Mute, deafen, see who's talking and jump between voice channels without leaving your game.
Sister project of [Discord-Lite](https://github.com/lopesleo/Discord-Lite) for Steam Deck.

[English](#english) · [Português](#português)

---

## English

### Features

- Mute / deafen / leave call, from the Game Bar overlay (Xbox button)
- Microphone and output volume
- Who's in the channel, with a live speaking indicator
- Per-user volume and "mute for me"
- Browse servers → voice channels and join
- English and Portuguese

### Requirements

- Windows 10 2004+ / Windows 11, x64 (ROG Xbox Ally, Ally X, any PC)
- Discord desktop app, running
- A Discord application of your own (free, 2 minutes — see below)

### Install

1. Download `Xcord-<version>-x64.zip` from [Releases](../../releases) and extract it.
2. Right-click `Install.ps1` → **Run with PowerShell**. It asks for admin once to trust the
   package certificate, then installs Xcord and its dependencies.
3. Open Game Bar (`Win + G` or the Xbox button) → **Widgets** → **Xcord**. Click the star to
   keep it in the Game Bar menu.

### Set up your Discord app (once)

Discord's voice API (local RPC) is in private beta: it only works with Discord applications **you own**.
So instead of a shared app, Xcord uses one you create — it's free and takes two minutes:

1. Go to the [Discord Developer Portal](https://discord.com/developers/applications) and click
   **New Application**. Any name works (e.g. `Xcord`).
2. Open **OAuth2** in the sidebar:
   - turn on **Public Client**;
   - under **Redirects**, add `http://127.0.0.1`;
   - click **Save Changes**.
3. Copy the **Client ID** (same page) and paste it into Xcord's setup screen → **Save**.
4. Click **Connect**. Discord shows an authorization window in the desktop app — click **Authorize**.

Use the same Discord account that created the app. To use a second account, either create another
app with it (Xcord menu `⋯` → **Discord app…**) or add it under **App Testers** in your app.

### Troubleshooting

| Message | Fix |
| --- | --- |
| *Discord didn't recognize this Client ID* | Copy the number under OAuth2 → Client ID (17–20 digits). |
| *Turn on Public Client* | OAuth2 → Public Client → on → Save Changes. |
| *Isn't the owner of the configured app* | You're logged into Discord with a different account than the one that created the app. |
| *Discord isn't running* | Open the Discord desktop app (the browser version doesn't expose RPC). |

Logs: `%LOCALAPPDATA%\Packages\lopesleo.Xcord_*\LocalState\bridge.log`

### How it works

Game Bar widgets are sandboxed UWP apps and can't open Discord's IPC pipe. Xcord ships a small
full-trust helper (`Xcord.Bridge`) inside the same package: it talks to the Discord app over
`\\.\pipe\discord-ipc-0` and to the widget over an AppService. Your token stays on your PC
(package LocalState); the only network call is the OAuth token exchange with Discord.

### Build from source

Requires Visual Studio 2022 (or Build Tools) with **Universal Windows Platform build tools**,
Windows SDK 10.0.22621+, .NET 9 SDK and Windows Developer Mode.

```powershell
.\build.ps1             # Debug: build and register on this PC
.\build.ps1 -Release    # signed package + dist\Xcord-<version>-x64.zip
```

`Xcord.Bridge.exe --console` runs the bridge alone (state on stdout, commands such as `get_state`,
`authorize`, `toggle_mute` on stdin). Architecture and roadmap: [PLAN.md](PLAN.md).

---

## Português

### O que faz

- Mutar / ensurdecer / sair da chamada pelo overlay do Game Bar (botão Xbox)
- Volume do microfone e do áudio
- Quem está no canal, com indicador de fala em tempo real
- Volume por pessoa e "silenciar para mim"
- Navegar servidores → canais de voz e entrar

### Instalação

1. Baixe `Xcord-<versão>-x64.zip` em [Releases](../../releases) e extraia.
2. Clique com o botão direito em `Install.ps1` → **Executar com o PowerShell**. Ele pede admin uma
   vez para confiar no certificado do pacote e instala o Xcord e as dependências.
3. Abra o Game Bar (`Win + G` ou botão Xbox) → **Widgets** → **Xcord** e marque a estrela.

### Configure seu app do Discord (uma vez)

A API de voz do Discord (RPC local) está em beta fechado e só funciona com apps **seus**. Por isso
o Xcord usa um app criado por você — é grátis e leva dois minutos:

1. Entre no [Discord Developer Portal](https://discord.com/developers/applications) e clique em
   **New Application** (qualquer nome, ex.: `Xcord`).
2. Abra **OAuth2** no menu lateral:
   - ative **Public Client**;
   - em **Redirects**, adicione `http://127.0.0.1`;
   - clique em **Save Changes**.
3. Copie o **Client ID** (mesma página), cole na tela de configuração do Xcord → **Salvar**.
4. Clique em **Conectar** e, no app do Discord, clique em **Autorizar**.

Use a mesma conta do Discord que criou o app. Para outra conta, crie outro app com ela
(menu `⋯` do Xcord → **App do Discord…**) ou adicione-a em **App Testers** no seu app.

---

Xcord is not affiliated with or endorsed by Discord Inc. or Microsoft.
