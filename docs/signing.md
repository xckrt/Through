# Подпись Тропы (SignPath Foundation)

Без подписи Windows SmartScreen показывает «Неизвестный издатель» и прячет кнопку «Выполнить» за «Подробнее».
[SignPath Foundation](https://signpath.org) бесплатно подписывает open-source проекты своим сертификатом.
Сборка идёт в GitHub Actions, и SignPath проверяет, что подписывается именно то, что собрано из репозитория.

`release.yml` уже умеет подписывать: шаги подписи включаются сами, когда в репозитории появляется секрет
`SIGNPATH_API_TOKEN`. Без него релиз собирается неподписанным, как сейчас.

## 1. Заявка

https://signpath.org/apply. Что вписать:

| Поле | Значение |
| --- | --- |
| Project name | Tropa |
| Repository | https://github.com/xckrt/Tropa |
| License | MIT (файл LICENSE в корне) |
| Description | Windows utility that diagnoses network blocking (DNS, TCP, TLS, HTTP stage by stage) and configures zapret's winws, a DPI processing engine, automatically. It also includes a local Telegram MTProto proxy and secure DNS (DoH). |
| Build system | GitHub Actions, `.github/workflows/release.yml`, triggered by a `v*` tag |
| Artifacts | `Tropa-Setup-x.y.z.exe` (installer), `Tropa.exe`, `Tropa.Service.exe` and the `Tropa.*.dll` assemblies inside it |
| Third-party binaries | none in the repository; zapret is downloaded at runtime from official bol-van/zapret releases with SHA-256 verification |

У SignPath есть требования к проекту: открытый код, понятная лицензия, политика конфиденциальности, отсутствие
вредоносного поведения. Политика — в README (раздел «Что Тропа отправляет в сеть»). Если её попросят отдельным
файлом, напишите мне, я сделаю `PRIVACY.md`.

## 2. После одобрения

В SignPath (проект `Tropa`):

1. Подключите репозиторий как Trusted Build System → GitHub.com.
2. Создайте политику подписи `release-signing`.
3. Создайте две конфигурации артефактов (slug важен, он указан в release.yml).

`app` (папка программы, GitHub-артефакт приходит zip-архивом):

```xml
<artifact-configuration xmlns="http://signpath.io/artifact-configuration/v1">
  <zip-file>
    <pe-file path="Tropa.exe"><authenticode-sign /></pe-file>
    <pe-file path="Tropa.Service.exe"><authenticode-sign /></pe-file>
    <pe-file-set>
      <include path="Tropa.*.dll" />
      <for-each><authenticode-sign /></for-each>
    </pe-file-set>
  </zip-file>
</artifact-configuration>
```

`setup` (установщик):

```xml
<artifact-configuration xmlns="http://signpath.io/artifact-configuration/v1">
  <zip-file>
    <pe-file path="Tropa-Setup-*.exe"><authenticode-sign /></pe-file>
  </zip-file>
</artifact-configuration>
```

## 3. GitHub

Settings → Secrets and variables → Actions:

- секрет `SIGNPATH_API_TOKEN` — токен CI-пользователя SignPath;
- переменная `SIGNPATH_ORGANIZATION_ID` — id организации из SignPath;
- если slug проекта или политики отличаются от `Tropa` и `release-signing`, задайте переменные `SIGNPATH_PROJECT_SLUG`
  и `SIGNPATH_POLICY_SLUG`.

Следующий тег `v*` соберётся подписанным. В шаге «Хеши» журнал Actions покажет `подпись установщика: Valid`,
а в описании релиза появится строка о подписи.
