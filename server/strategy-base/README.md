# Общая база стратегий

Cloudflare Worker и база D1: Тропа присылает «у меня YouTube починила стратегия X», а перед подбором
спрашивает, что работает у этого провайдера, и пробует это первым.

- Провайдера Worker узнаёт сам, по соединению (`request.cf.asn`). Клиент не присылает ни IP, ни идентификаторов.
- IP в базу не записывается. Для лимита (60 отчётов в сутки с адреса) и подсчёта «сколько людей» хранится
  `sha256(IP | день | соль)`, и этот хеш меняется каждые сутки.
- Отчёты отправляются, только если человек включил «Делиться результатами подбора» в настройках Тропы.
- Рекомендация появляется, когда стратегию подтвердили хотя бы двое разных людей за последние 60 дней.

## Деплой (один раз, ~5 минут)

Нужны бесплатный аккаунт Cloudflare и Node.js.

```
cd server/strategy-base
npx wrangler login
npx wrangler d1 create tropa-strategies        # скопируйте database_id в wrangler.toml
npx wrangler d1 execute tropa-strategies --remote --file=schema.sql
npx wrangler secret put SALT                   # любая длинная случайная строка
npx wrangler deploy                            # выведет адрес https://tropa-strategy-base.<аккаунт>.workers.dev
```

Адрес вписывается в `src/Tropa.Cli/community.json` (`"url"`), и со следующего релиза Тропа начнёт им пользоваться.
Пока `url` пустой, база выключена.

## API

| Запрос | Что делает |
| --- | --- |
| `GET /v1/strategies` | Стратегии для провайдера спрашивающего: `{asn, org, services: {youtube: [{preset, ok, fail, users, score}]}}` |
| `POST /v1/report` | `{tropa, engine, results: [{service, preset, ok}]}` |
| `GET /v1/map` | Сводка по провайдерам и сервисам за 60 дней (для карты блокировок) |
