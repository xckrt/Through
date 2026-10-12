# Through

[Русский](README.md) · **English** · [Website](https://xckrt.github.io/Through/)

Through is a Windows app that works out what your ISP blocks and how, then sets up a bypass for YouTube, Discord
(voice included), Telegram, games and any website. You press one button and Through keeps it working from then on,
across reboots and network changes.

![Through overview](site/img/overview-dark.png)

> The interface is in English and Russian (Settings → Language). Through is built for networks with DPI-based blocking (TSPU and similar).

## Install

Download **Through-Setup-x.y.z.exe** from [Releases](https://github.com/xckrt/Through/releases/latest) and run it.
Windows asks for administrator permission once, because Through installs a service that keeps the bypass running.
No .NET install is needed. Uninstall it like any other app.

Or from the command line: `winget install xckrt.Through` (regular), or
`scoop bucket add through https://github.com/xckrt/Through` and then `scoop install through` (portable).

No install: **Through-Portable-x.y.z.zip**. Unpack it and run `Through.exe`. The bypass runs while Through is open,
and data is kept in the `data` folder next to it.

## What it does

- **One-button bypass.** Through probes each service stage by stage (DNS → TCP → TLS → HTTP → transfer) and names
  the blocking method: RST on the SNI, silent drop, the "16 KB freeze", DNS spoofing. For that diagnosis it tries
  [zapret](https://github.com/bol-van/zapret) strategies from cheap to costly and turns on the one that passes
  every check. The service keeps watching and re-tunes on its own when the ISP changes something.
- **Telegram.** A built-in proxy sends traffic through Telegram Web's WebSocket endpoints, which are blocked less
  often, and falls back to Cloudflare. It is a C# port of [tg-ws-proxy](https://github.com/Flowseal/tg-ws-proxy)
  that runs inside the service, with no Python. Through adds the proxy to Telegram for you and checks every
  datacenter.
- **Discord voice.** Voice is checked for real, with a UDP IP Discovery packet to the voice servers. If it fails,
  Through picks a working voice profile variant by itself.
- **Secure DNS.** System DNS goes over DNS-over-HTTPS (Cloudflare, Google, Quad9), so the ISP can neither see nor
  spoof it. If DoH stops answering, Through hands back your old DNS so the internet keeps working.
- **Your own apps.** Click "pick a running program" and play for half a minute. Through listens (ETW) and collects
  the game's domains and ports. Templates for Dead by Daylight, Roblox and Twitch are included.
- **Per-network profiles.** Home Wi-Fi and a phone hotspot get different strategies. Through recognizes the network
  and switches.
- **"Why doesn't it open?"** Paste any address. Through tells you who is at fault (the ISP, the IP, DNS or the site
  itself) and bypasses it with one click when it can.
- **YouTube speed** before and after the bypass, for example "0.8 → 92 Mbit/s".
- **Shared strategy base.** If you opt in, Through installs share what worked on their ISP, so tuning takes seconds
  for the next user.
- **Through for Android.** The Telegram proxy right on the phone, on Wi-Fi and mobile data, plus a check of what
  the mobile network blocks. See [docs/android.md](docs/android.md).
- zapret and Through updates verified by SHA-256, a dark theme, and tray toggles.

## What Through sends over the network

| Where | Why | What is sent |
| --- | --- | --- |
| github.com (bol-van/zapret, xckrt/Through, Flowseal) | download zapret, check for updates, Cloudflare domain list for Telegram | a plain HTTPS request |
| probed sites (youtube.com, discord.com, …) | see what is blocked | the same as a browser |
| 1.1.1.1, 8.8.8.8, 9.9.9.9 | secure DNS, when enabled | DNS queries inside HTTPS |
| www.youtube.com, googlevideo.com | YouTube speed test, on demand and during tuning | a chunk of a public video |
| shared strategy base (Cloudflare Worker) | hints before tuning | nothing beyond the request itself |
| shared strategy base | **only if "share results" is on** | service id, strategy id, worked or not, Through and zapret versions |

No IP addresses, site addresses or identifiers are stored. The server derives the ISP from the connection.
Bug reports are never sent silently: "report a problem" opens a prefilled GitHub issue that you review first.
The Telegram proxy listens on this computer only, and Telegram encrypts the messages itself, so Through never sees
them.

## Build

.NET 8 SDK.

```
dotnet build
dotnet run --project tests/Through.SelfTest                 # tests, no internet needed
dotnet run --project src/Through.Tray                       # the window
dotnet run --project src/Through.Cli -- --telegram          # Telegram proxy against all DCs
```

Installer and portable build: `pwsh build/publish.ps1 -Version 1.2.0`. Releases are built by GitHub Actions on a `v*` tag.

## Code signing

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).
Privacy policy: [PRIVACY.md](PRIVACY.md).

## License

MIT. The Telegram proxy is a port of [Flowseal/tg-ws-proxy](https://github.com/Flowseal/tg-ws-proxy) (MIT), and the
strategies come from [Flowseal/zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube) (MIT). The
engine, [bol-van/zapret](https://github.com/bol-van/zapret), is downloaded at runtime. See
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
