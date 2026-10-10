# winget и Scoop

## Как ставят пользователи

```
winget install xckrt.Tropa
```

Обычная версия ставится со службой и автозапуском, без окон, одним разрешением администратора.

```
scoop bucket add tropa https://github.com/xckrt/Tropa
scoop install tropa
```

Через Scoop ставится переносная версия: обход работает, пока открыта Тропа, а папка `data` переживает
`scoop update`.

## Что происходит при релизе

`release.yml` после хешей запускает `build/packages.ps1`, и тот делает две вещи:

- **Scoop:** обновляет `bucket/tropa.json` (версия, ссылка, SHA-256) и коммитит его в `main`. Бакет живёт в этом
  репозитории, отдельный репозиторий не нужен. `checkver` и `autoupdate` в манифесте позволяют обновить его и вручную:
  `scoop config ...; ./bin/checkver.ps1 tropa -u` из репозитория Scoop.
- **winget:** собирает манифест в `artifacts/winget/<версия>/` (схема 1.6, проверен `winget validate`). Если есть
  секрет `WINGET_TOKEN`, `wingetcreate submit` сам открывает PR в
  [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs).

## Один раз для winget

1. Создайте classic Personal Access Token: GitHub → Settings → Developer settings → Tokens (classic), право
   `public_repo`. wingetcreate форкнет winget-pkgs в ваш аккаунт и откроет PR оттуда.
2. Добавьте его в репозиторий Тропы как секрет `WINGET_TOKEN` (Settings → Secrets and variables → Actions).
3. Первый PR проверяют модераторы winget, обычно 1–3 дня. Установщик тестируется в их песочнице, поэтому тихая
   установка (`Tropa-Setup.exe /silent`) должна проходить без окон. Это так: `/silent` ставит с ярлыком и
   автозапуском и возвращает 0. Следующие версии проходят быстрее.

Без токена манифест всё равно собирается. Его можно отправить вручную:
`wingetcreate submit --token <PAT> artifacts/winget/<версия>`.
