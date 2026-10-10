# Сторонний код

## tg-ws-proxy

`src/Tropa.Core/Telegram/` — перенос логики [Flowseal/tg-ws-proxy](https://github.com/Flowseal/tg-ws-proxy)
с Python на C#: разбор MTProto init, перешифровка, WebSocket к Telegram Web, пул, запасные пути
через Cloudflare и закодированный список доменов Cloudflare.

```
MIT License

Copyright (c) 2026 Flowseal

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## zapret-discord-youtube

Стратегии в `presets.json` и шаблоны ClientHello взяты из
[Flowseal/zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube) (MIT).
Сам zapret ([bol-van/zapret](https://github.com/bol-van/zapret)) скачивается при работе из официальных
релизов и в репозитории не лежит.
