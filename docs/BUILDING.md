# Сборка из исходного кода

## Требования

- Windows 10 или Windows 11 x64;
- Visual Studio 2022;
- workload **Desktop development with .NET**;
- .NET Framework 4.8 Developer Pack;
- PowerShell 5.1 или новее;
- интернет для восстановления NuGet-пакетов.

API-ключи для сборки и автоматических тестов не нужны.

## Полная Release-сборка

Откройте PowerShell в корне репозитория и выполните:

```powershell
.\build-release.ps1
```

Скрипт:

1. находит MSBuild из Visual Studio;
2. восстанавливает NuGet-зависимости;
3. собирает решение в конфигурации Release x64;
4. запускает автоматические тесты;
5. создаёт portable ZIP в папке `artifacts`.

Итоговый архив:

```text
artifacts\pis.etc-win10-x64-v1.1.1.zip
```

## Сборка в Visual Studio

1. Откройте `SpeechToText.sln`.
2. Выберите конфигурацию `Release` и платформу `x64`.
3. Выполните **Build → Build Solution**.
4. Запустите `tests\SpeechToText.Tests\bin\Release\SpeechToText.Tests.exe`.

## Почему нет реальных API-тестов

Автоматические тесты проверяют формирование HTTP/WebSocket-сообщений с
подменёнными ответами. Реальные запросы намеренно исключены, чтобы:

- не хранить секреты в репозитории;
- не списывать средства;
- не делать сборку зависимой от доступности внешнего сервиса.
