# Xcord — Discord para o Xbox Game Bar (ROG Xbox Ally X)

Port do [Discord-Lite](../../Discord-Lite) (plugin Decky do Steam Deck) para um widget do Xbox Game Bar.

## Por que dois processos

Widgets do Game Bar são apps UWP e rodam em AppContainer (sandbox). Do sandbox não dá para abrir o
named pipe do Discord (`\\.\pipe\discord-ipc-0`). Por isso o pacote leva um processo **full-trust**
(a "bridge") que fala com o Discord, e o widget conversa com a bridge por **AppService**.

```
┌──────────────────────────┐  AppService (ValueSet)  ┌───────────────────────────┐  named pipe  ┌─────────┐
│ Xcord.Widget (UWP)       │ ◄─────────────────────► │ Xcord.Bridge (.NET 9)     │ ◄──────────► │ Discord │
│ XAML no Game Bar         │  req/resp + eventos     │ full-trust, sem janela    │  RPC (IPC)   │ desktop │
│ renderiza o estado       │                         │ OAuth PKCE, estado de voz │              └─────────┘
└──────────────────────────┘                         └───────────────────────────┘
```

Equivalência com o Discord-Lite:

| Discord-Lite (Deck)              | Xcord (Windows)                                      |
| -------------------------------- | ---------------------------------------------------- |
| `src/index.tsx` (React, @decky/ui) | `Xcord.Widget` (UWP XAML, foco por controle/XYFocus) |
| `main.py` + `callable()`          | `Xcord.Bridge` + AppService                          |
| Unix socket `discord-ipc-N`       | Named pipe `\\.\pipe\discord-ipc-N`                  |
| `auth/oauth.py` (PKCE)            | `Discord/OAuth.cs` (PKCE + refresh token)            |
| `polling/voice_poller.py` (15s)   | eventos `SUBSCRIBE` em tempo real (sem polling)      |
| `steam/` (detecção de jogo)       | não precisa: o Discord no Windows já detecta jogos   |

Scopes: `rpc`, `rpc.voice.read`, `rpc.voice.write`.

### Client ID: cada usuário traz o seu (decidido em 2026-10-26)

A API RPC de voz está em beta fechado: só o dono do app e até 50 testers convidados conseguem
autorizar (`invalid_scope` para o resto), e o Discord praticamente não aprova apps para uso público.
Por isso não há Client ID embutido: no primeiro uso o widget mostra um passo a passo (criar app no
Developer Portal, ativar **Public Client**, redirect `http://127.0.0.1`, colar o Client ID), salvo em
`LocalState\settings.json`. O app Deckord (`1461502476401381446`, equipe KGBTeam) segue servindo
para o autor e os testers dele. Distribuição: GitHub Releases (zip com msix + `Install.ps1`);
Microsoft Store depois.

## Protocolo widget ⇄ bridge

- Requisição: `ValueSet { m: "<método>", a: "<json args>" }` → resposta `{ ok: bool, d: "<json>", e: "<erro>" }`
- Evento (bridge → widget): `{ evt: "state", d: "<json snapshot>" }` — a bridge empurra o estado
  completo sempre que algo muda (coalescido), o widget só renderiza.

Métodos: `get_state`, `authorize`, `logout`, `launch_discord`, `toggle_mute`, `toggle_deaf`,
`set_input_volume`, `set_output_volume`, `leave`, `get_guilds`, `get_channels`, `join`,
`set_user_volume`, `mute_user`.

## Fases

1. **MVP (agora)**
   - Bridge: cliente IPC assíncrono (framing, nonce → resposta, reconexão), OAuth PKCE + refresh,
     token salvo no LocalFolder do pacote, estado de voz por eventos.
   - Widget: status de conexão, autorizar, mute/deafen/sair, volumes de entrada/saída,
     membros com indicador de fala + volume/mute por usuário, navegação servidor → canal → entrar.
   - PT-BR/EN, build + deploy via `build.ps1`.
2. **Paridade com o Deck**: modo de voz/PTT, supressão de ruído, eco, AGC; notificações de
   entrada/saída de membros (Game Bar notifications); timer da chamada.
3. **Ally X**: atalhos de hotkey do Game Bar para mute/deafen, layout compacto pinável (overlay
   mínimo com quem está falando), pacote MSIX assinado para instalar no Ally.

## Build

Requer VS 2022 Build Tools (workload UWP), Windows SDK 10.0.22621+, .NET 9 SDK, Modo de Desenvolvedor.

```powershell
.\build.ps1            # publica a bridge, compila o widget e registra o pacote (Debug)
.\build.ps1 -Release   # gera o .msix para instalar no Ally
```
