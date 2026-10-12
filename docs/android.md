# Тропа для Android

`src/Through.Android` — приложение на .NET for Android (`net10.0-android`, Android 7+). Общий код тот же,
что у Windows-версии: `Through.Core` (прокси Telegram, пробник, каталог целей).

Что умеет:

- **Прокси Telegram на телефоне.** `TelegramService` — служба переднего плана с уведомлением, внутри тот же
  `TelegramProxy` на `127.0.0.1:1443` (или следующий свободный порт). Секрет хранится в SharedPreferences
  `through/tg_secret`. «Подключить в Telegram» открывает ссылку `tg://proxy?...`, «Проверить дата-центры»
  гоняет `TelegramProbe` через этот прокси. Работает и на мобильном интернете, компьютер не нужен.
- **«Что режут здесь».** `ServiceProber` по `targets.json` (лежит в Assets) для YouTube и Discord, ответ
  словами: по имени, заморозка 16 КБ, DNS, адрес, голос.
- **Подсказки** про «Для всего дома» на компьютере (прокси Wi-Fi → Авто → PAC) и «Частный DNS».
- **Плитка в шторке** (`TelegramTileService`, `TileService`): включает и выключает прокси в одно касание.
  Если прошивка не даёт запустить службу переднего плана из шторки, открывается приложение.
- **Ссылка `through://home?pac=…`**: QR «Для всего дома» на компьютере ведёт в Тропу (а не в браузер, как
  голый адрес PAC). `MainActivity` по ней копирует адрес в буфер и открывает настройки Wi-Fi.

Чего нет: обхода для всех приложений телефона. Без VPN Android этого не позволяет. Режим VPN
(`VpnService` + tun2socks + десинхронизация как у zapret) — следующий большой шаг.

## Сборка

Нужны .NET 10 SDK, `dotnet workload install android` и Android SDK (ставится вместе с Visual Studio
или Android Studio; путь берётся из `ANDROID_HOME`). Проект не входит в `Through.sln`, обычная сборка
workload не требует.

```
dotnet build src/Through.Android
dotnet publish src/Through.Android -c Release -o artifacts/android   # artifacts/android/com.xckrt.through-Signed.apk
```

Без ключа APK подписан отладочным ключом: его можно поставить руками, но следующая версия поверх не встанет.

## Ключ для релизов

Создаётся один раз и хранится вне репозитория:

```
keytool -genkeypair -v -keystore through.jks -alias through -keyalg RSA -keysize 4096 -validity 10000
```

В секреты репозитория: `ANDROID_KEYSTORE` (base64 от `through.jks`), `ANDROID_KEY_ALIAS`, `ANDROID_KEY_PASS`.
Пароль хранилища и ключа должен совпадать. С этими секретами `release.yml` прикладывает
`Through-Android-x.y.z.apk` и `.sha256` к релизу; без них APK только собирается для проверки.
Потерянный ключ — это новое приложение: старые установки обновиться не смогут.
