# Through

[Русский](README.md) · **English** · [Website](https://xckrt.github.io/Through/)

A Windows app that gets around ISP blocking — YouTube, Discord with voice, Telegram, games, websites — and does it
on its own. It figures out what exactly is being blocked, picks a working strategy, and then keeps the bypass up
without you: across reboots, across Wi-Fi changes, when the ISP retunes its filter. One button.

![Through overview](site/img/overview-dark.png)

> The interface is in English and Russian (Settings → Language). Through is built for networks with DPI-based
> blocking (TSPU and similar).

## Install

Download **Through-Setup-x.y.z.exe** from [Releases](https://github.com/xckrt/Through/releases/latest) and run it.
Windows asks for administrator permission once — Through installs a service that keeps the bypass running. No .NET
needed, and it uninstalls like any other app.

Package managers: `winget install xckrt.Through`, or Scoop —
`scoop bucket add through https://github.com/xckrt/Through` and `scoop install through`.

Don't want to install? **Through-Portable-x.y.z.zip** — unpack, run `Through.exe`. The bypass lives while the window
is open; data sits in the `data` folder next to it.

## How it works

Through runs each service through stages — DNS → TCP → TLS → HTTP → transfer — and from where it breaks it tells the
blocking method apart: RST on the SNI, a silent drop, the "16 KB freeze", DNS spoofing. For that diagnosis it tries
[zapret](https://github.com/bol-van/zapret) strategies from cheap to costly and keeps the one that passes every
check. The service keeps watching and re-tunes on its own if something stops working.

Besides websites it also handles:

- **Telegram** — a built-in proxy (a port of [tg-ws-proxy](https://github.com/Flowseal/tg-ws-proxy), no Python):
  traffic goes through Telegram Web, falling back to Cloudflare if that is blocked. The proxy is added to the client
  for you.
- **Discord voice** — checked for real, a UDP packet to the voice servers; if it fails, a voice profile is picked.
- **Secure DNS** — system queries over DNS-over-HTTPS (Cloudflare, Google, Quad9). If DoH goes quiet, your old DNS
  comes back on its own, no outage.
- **Your apps and games** — click "pick a running program", play for half a minute, and the domains and ports are
  collected for you. Templates for Dead by Daylight, Roblox and Twitch are included.
- **Per-network profiles** — home and a phone hotspot can run different strategies; Through recognizes the network
  and switches.
- **"Why doesn't it open?"** — paste an address and Through tells you who's at fault: the ISP, the IP, DNS, or the
  site itself.
- **Android** — the Telegram proxy on the phone (Wi-Fi and mobile data) plus a network check. See
  [docs/android.md](docs/android.md).

There's also a dark theme, tray toggles, a YouTube speed test before/after the bypass, SHA-256-verified updates for
zapret and Through itself, and a shared strategy base — opt in to share what worked on your ISP so the next person's
tuning takes seconds.

## What Through sends over the network

Nothing goes out without your say-so. It only talks to:

| Where | Why | What is sent |
| --- | --- | --- |
| github.com (bol-van/zapret, xckrt/Through, Flowseal) | download zapret, check for updates, Cloudflare domain list for Telegram | a plain HTTPS request |
| probed sites (youtube.com, discord.com, …) | see what is blocked | the same as a browser |
| 1.1.1.1, 8.8.8.8, 9.9.9.9 | secure DNS, when enabled | DNS queries inside HTTPS |
| www.youtube.com, googlevideo.com | YouTube speed test, on demand and during tuning | a chunk of a public video |
| shared strategy base (Cloudflare Worker) | hints before tuning | nothing beyond the request itself |
| shared strategy base | **only if "share results" is on** | service id, strategy id, worked or not, Through and zapret versions |

No IP addresses, site addresses or identifiers are stored — the server derives the ISP from the connection itself.
Bug reports are never sent silently: "report a problem" opens a prefilled GitHub issue you review first. The Telegram
proxy listens on this computer only, and Telegram encrypts the messages itself, so Through never sees them.

## Build

.NET 8 SDK.

```
dotnet build
dotnet run --project tests/Through.SelfTest                 # tests, no internet needed
dotnet run --project src/Through.Tray                       # the window
dotnet run --project src/Through.Cli -- --telegram          # Telegram proxy against all DCs
```

Installer and portable build: `pwsh build/publish.ps1 -Version 1.2.0`. Releases are built by GitHub Actions on a
`v*` tag.

## License

MIT. The Telegram proxy is a port of [Flowseal/tg-ws-proxy](https://github.com/Flowseal/tg-ws-proxy) (MIT), the
strategies come from [Flowseal/zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube) (MIT),
and the bypass engine, [bol-van/zapret](https://github.com/bol-van/zapret), is downloaded at runtime. See
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Privacy policy: [PRIVACY.md](PRIVACY.md).
