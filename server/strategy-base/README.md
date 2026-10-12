# Общая база стратегий

Cloudflare Worker и база D1: Сквозь присылает «у меня YouTube починила стратегия X», а перед подбором
спрашивает, что работает у этого провайдера, и пробует это первым.

- Провайдера Worker узнаёт сам, по соединению (`request.cf.asn`). Клиент не присылает ни IP, ни идентификаторов.
- IP в базу не записывается. Для лимита (60 отчётов в сутки с адреса) и подсчёта «сколько людей» хранится
  `sha256(IP | день | соль)`, и этот хеш меняется каждые сутки.
- Отчёты отправляются, только если человек включил «Делиться результатами подбора» в настройках Сквозь.
- Рекомендация появляется, когда стратегию подтвердили хотя бы двое разных людей за последние 60 дней.

## Деплой (один раз, ~5 минут)

Нужны бесплатный аккаунт Cloudflare и Node.js.

```
cd server/strategy-base
npx wrangler login
npx wrangler d1 create tropa-strategies        # скопируйте database_id в wrangler.toml
npx wrangler d1 execute tropa-strategies --remote --file=schema.sql
npx wrangler d1 execute tropa-strategies --remote --file=migrations/0002_status.sql
npx wrangler d1 execute tropa-strategies --remote --file=migrations/0003_crashes.sql
npx wrangler secret put SALT                   # любая длинная случайная строка
npx wrangler secret put ADMIN_KEY              # ключ для чтения сводок об ошибках (GET /v1/crashes)
npx wrangler deploy                            # выведет адрес https://tropa-strategy-base.<аккаунт>.workers.dev
```

Адрес вписывается в `src/Through.Cli/community.json` (`"url"`), и со следующего релиза Сквозь начнёт им пользоваться.
Пока `url` пустой, база выключена.

## API

| Запрос | Что делает |
| --- | --- |
| `GET /v1/strategies` | Стратегии для провайдера спрашивающего: `{asn, org, services: {youtube: [{preset, ok, fail, users, score}]}}` |
| `POST /v1/report` | `{tropa, engine, results: [{service, preset, ok}]}` |
| `GET /v1/alerts` | Ранний сигнал: что у провайдера массово сломалось за 3 часа и что помогло другим |
| `GET /v1/map` | Провайдеры × сервисы за 7 дней: доля «режут», способ, что помогает |
| `GET /v1/catalog` | Обновляемый список доменов для обхода: `{version, services:{service:[domains]}}`. Только домены известных сервисов; правится в `src/worker.js`, version повышается при изменении |
| `POST /v1/crash` | Сводка об ошибке (по согласию): тип и имена методов, без текста и путей |
| `GET /v1/crashes` | Сводки за 30 дней, только с `Authorization: Bearer <ADMIN_KEY>` |

Посмотреть ошибки: `curl -H "Authorization: Bearer <ключ>" https://tropa-strategy-base.xckrt.workers.dev/v1/crashes`.

Обновление уже развёрнутой базы — только новые миграции из `migrations/` по порядку, затем `npx wrangler deploy`.
