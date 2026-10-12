# Privacy Policy / Политика конфиденциальности

_Last updated: 2026-10-10_

[English](#english) · [Русский](#русский)

## English

Through is a desktop app that runs on your computer. It has no accounts, no analytics and no advertising.

**Network requests Through makes**

| Destination | Purpose | Data sent |
| --- | --- | --- |
| github.com, raw.githubusercontent.com | download zapret, check for Through and zapret updates, the Cloudflare domain list for the Telegram proxy | a standard HTTPS request |
| sites under test (youtube.com, discord.com, …) | find out what is blocked | the same requests a browser makes |
| 1.1.1.1, 8.8.8.8, 9.9.9.9 | secure DNS (DNS-over-HTTPS), only if you turn it on | your DNS queries, encrypted inside HTTPS |
| www.youtube.com, googlevideo.com | YouTube speed test, on demand and during tuning | a download of part of a public video |
| Telegram servers (via Telegram Web or Cloudflare) | the local Telegram proxy, only if you turn it on | Telegram's own traffic, already encrypted by Telegram; Through cannot read it |
| through-strategy-base.xckrt.workers.dev | hints on which strategy works for your ISP before tuning | nothing beyond the request itself |
| through-strategy-base.xckrt.workers.dev | **only if you enable "share tuning results"** (off by default) | service id, strategy id, whether it worked, Through and zapret versions |
| through-strategy-base.xckrt.workers.dev | **only if you enable "share tuning results"**: the blocking map and early warnings | per service: blocked or not, whether under bypass, the active strategy id and the blocking type (e.g. TlsReset); at most hourly, plus when a service breaks or recovers |

**The shared strategy base** is a Cloudflare Worker. It determines your ISP (autonomous system number and name)
from the connection, along with the country and region (province) that Cloudflare reports for it, but no city or address. It does **not** store IP addresses, site addresses or device identifiers. For rate limiting
and for counting distinct users it keeps a SHA-256 hash of IP, day and a secret salt. That hash changes every day
and cannot be traced back to an address. Reports older than 60 days no longer affect recommendations.

**Local data.** Settings, tuned strategies, logs and the downloaded engine are stored on your computer in
`%ProgramData%\Through`, or in the `data` folder of the portable version. They are never uploaded.

**Error summaries** are sent only if you enable "Send error summaries" (off by default). A summary has the exception
type, up to 8 method names from Through and .NET (for example `Through.Tray.MainViewModel.CheckAsync`), and the Through,
.NET and Windows versions. The error message is never sent, because messages can contain paths, site addresses or
names. Identical errors are merged into a counter, and only the developer can read them.

**Bug reports** are never sent automatically. "Report a problem" opens a prefilled GitHub issue in your browser,
and you see the full text before submitting it.

**Program inspection.** "Pick a running program" observes only the network addresses and DNS names used by the
program you select, for up to 90 seconds, on your computer. The traffic content is never read or sent anywhere.

Questions: https://github.com/xckrt/Through/issues

## Русский

Тропа работает на вашем компьютере. В ней нет аккаунтов, аналитики и рекламы.

**Куда Тропа обращается** — см. таблицу выше:
- GitHub: обновления и zapret;
- проверяемые сайты: как браузер;
- DoH-серверы: только с включённым защищённым DNS;
- YouTube: замер скорости;
- серверы Telegram: только с включённым прокси, переписку шифрует сам Telegram;
- общая база стратегий: подсказки перед подбором.

**Отправка итогов подбора** в общую базу выключена по умолчанию. Если её включить, уходят только id сервиса,
id стратегии, «помогла или нет» и версии Тропы и zapret. Для карты блокировок и раннего сигнала по каждому сервису
уходит «режут или нет», под обходом ли, id стратегии и тип блокировки. Это не чаще раза в час и сразу, когда сервис
сломался или ожил. Страну и область сервер берёт из соединения; города и адреса нет. Провайдера сервер определяет по соединению.
IP-адреса, адреса сайтов и идентификаторы устройств не хранятся. Для лимита и подсчёта людей хранится хеш
SHA-256 от IP, дня и секретной соли, который меняется каждые сутки.

**Данные на компьютере** — настройки, стратегии, журналы — лежат в `%ProgramData%\Through` (у переносной версии —
в папке `data`) и никуда не выгружаются.

**Сводки об ошибках** уходят, только если включить «Отправлять сводки об ошибках» (по умолчанию выключено). В сводке
есть тип исключения, до 8 имён методов Тропы и .NET и версии Тропы, .NET и Windows. Текст ошибки не уходит никогда:
в нём бывают пути, адреса сайтов и имена. Одинаковые ошибки склеиваются в счётчик, читать их может только разработчик.

**Отчёты об ошибках** сами не отправляются: открывается черновик issue на GitHub, и вы видите весь текст.

**«Выбрать запущенную программу»**: Тропа до 90 секунд смотрит только адреса и DNS-имена выбранной программы,
у вас на компьютере. Содержимое трафика не читается и никуда не отправляется.
