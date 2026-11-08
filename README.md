# DPT.PSP GTA Modding Kit

Инструментарий для моддинга **GTA Liberty City Stories** и **GTA Vice City Stories** (PSP).
**.NET Framework 4.0** (C# 4, только BCL, сборка стоковым `csc.exe`), веб-морда и headless CLI.
Лицензия MIT, игровых данных в репозитории нет.

```
build.cmd        -> bin\ModKit.exe
run.cmd          -> http://127.0.0.1:44700/
ModKit.exe identify LOADSC0.CHK
```

## Что уже работает

| Данные | Чтение | Запись |
|---|---|---|
| `.CSO` / `.ZSO` (сжатый образ PSP) | да | CSO v1 (deflate) |
| `.ISO` (ISO 9660 level 2) | да | да, сборка с нуля |
| `.IMG` (Stories, `.DIR` + сектора) | да | да (IMG + DIR) |
| `.IMG` (GTA III/VC `.DIR`, SA `VER2`) | да | да |
| `GAME.DTZ` ↔ `GAME.DAT` | да | да (zlib) |
| Таблица стриминга `GAME.DAT` → `.DIR` | да (LCS PSP проверено на диске) | экспорт DIR |
| `.LVZ` (zlib + чанк `WRLD`) | да | пересжатие |
| `.CHK` / `.XTX` (текстуры) | да: список, палитра, swizzle, mip, PNG | только чтение пока |
| `.MDL`, `.ANIM` (чанки `ldm`/`anim`) | заголовок и границы | — |
| `.GXT` (таблицы текста) | да, дамп в текст | — |
| `.VAG` (PS-ADPCM) | да → WAV | да ← WAV |
| `.AT3`, `.PMF`/`.PSS`, байткод Lua | идентификация и метаданные | — |

Проверено на реальных образах LCS и VCS (см. `docs/FORMATS.md`, там же честный список пробелов).

## Веб-морда

Одна страница, стиль 2008 (VK/FB-шный синий хром, плотная типографика) с современными
тенями и переходами. Темы `light` / `dark` / `oled`, мягкие синтезированные звуки
(WebAudio, без файлов и CDN), два языка (RU/EN) с автодетектом.

- **Проекты** — список, создание, поиск, задания.
- **Проект** — дерево файлов, **инспектор** (формат, текстуры с превью PNG, строки GXT,
  модели и их текстуры), **операции** (12 быстрых действий через очередь заданий),
  **сессия**, **журнал**.
- **Лог** — живой хвост лога сервера.
- **О наборе** — таблица форматов и примеры CLI.

## Совместная работа

У каждого проекта есть **код сессии** (`ABCD-2345`).

- **Оффлайн**: папка проекта переносится на другую машину, код остаётся тем же.
- **Онлайн**: второй человек открывает тот же веб-сервер, вводит ник и подключается по коду —
  видит участников и общий журнал операций. Клиенты получают изменения длинным опросом
  (long-poll), поэтому схема работает за reverse proxy и на старых машинах.

Подробности и границы: `docs/SESSIONS.md`.

## Headless

```
ModKit.exe identify <file>
ModKit.exe cso <src.cso> [out.iso]
ModKit.exe cso --compress <src.iso> [out.cso]
ModKit.exe iso-list <image.iso>
ModKit.exe iso-extract <image.iso> [folder]
ModKit.exe iso-build <folder> [out.iso] [label]
ModKit.exe img-list <image.img> [dir]
ModKit.exe img-extract <img> [folder] [--dir <dir>]
ModKit.exe dtz-inspect <GAME.DTZ|GAME.DAT>
ModKit.exe dtz-dat <GAME.DTZ> [out.dat]
ModKit.exe dtz-dtz <GAME.DAT> [out.dtz]
ModKit.exe dtz-dir <GAME.DTZ> [out.dir] [--vcs]
ModKit.exe dtz-models <GAME.DTZ> [--vcs] [--limit N]
ModKit.exe chk-list <file.chk|.xtx>
ModKit.exe chk-export <file.chk> [folder]
ModKit.exe gxt-dump <file.gxt> [out.txt]
ModKit.exe vag-to-wav <file.vag> [out.wav]
ModKit.exe wav-to-vag <file.wav> [out.vag]
ModKit.exe project-list
ModKit.exe project-new <name> [game]
ModKit.exe project-info <id>
ModKit.exe serve [--port 44700] [--bind 0.0.0.0]
```

Типичный пайплайн для LCS:

```bat
ModKit.exe cso "LCS.cso" "LCS.iso"
ModKit.exe iso-extract LCS.iso extracted
ModKit.exe dtz-inspect extracted\PSP_GAME\USRDIR\CHK\PSPHR\GAME.DTZ
ModKit.exe dtz-dir     extracted\PSP_GAME\USRDIR\CHK\PSPHR\GAME.DTZ lcs.dir
ModKit.exe img-list    extracted\PSP_GAME\USRDIR\MODELS\GTA3PSPHR.IMG lcs.dir
ModKit.exe img-extract extracted\PSP_GAME\USRDIR\MODELS\GTA3PSPHR.IMG content --dir lcs.dir
ModKit.exe chk-export  content\player_chk content_png
```

## Надёжность и логи

- **Логирование через `Trace`** без `app.config`: один файловый листенер (полный verbose)
  и один консольный (только то, на что оператор реально реагирует). `DefaultTraceListener`
  снимается, поэтому дублей в консоли нет. Ротация по дню и размеру, ретеншн.
  Детали: `docs/LOGGING.md`.
- **Динамическая отказоустойчивость**: `Guard` (исключение → лог, а не падение),
  `Retry` с экспоненциальным backoff и джиттером, `CircuitBreaker` на подсистему,
  `Supervisor` перезапускает веб-листенер с растущей задержкой, fallback-порты,
  лимиты соединений и размера тела. Детали: `docs/RESILIENCE.md`.

## Настройки

`config.ini` рядом с `ModKit.exe` (создаётся автоматически, пример — `config.example.ini`).
Ключевое: `server.webPort`, `server.bind`, `workspace.root`, `workspace.gamesRoot`,
`logging.fileLevel`, `logging.consoleLevel`, `resilience.*`, `limits.*`.

`bind=0.0.0.0` нужен для совместной работы по сети; по умолчанию сервер слушает только
`127.0.0.1`. Аутентификации в этой версии нет — наружу без reverse proxy не выставляйте.

## Устройство проекта

```
build.cmd / run.cmd                 сборка стоковым csc.exe / запуск
src/ModKit/
  Program.cs                        bootstrap, режимы, консольные команды
  Operations.cs                     все операции — общие для CLI и веб-API
  Cli/CommandLine.cs                headless-команды
  Core/Log.cs                       Trace, файл + консоль, ротация, ring-буфер
  Core/Config.cs                    config.ini
  Core/Json.cs                      свой JSON
  Core/Resilience.cs                Guard / Retry / CircuitBreaker / Supervisor
  Core/Workspace.cs                 проекты + присутствие в сессиях
  Core/Project.cs                   манифест, журнал операций, коды сессий
  Core/Pipeline.cs                  очередь фоновых заданий с прогрессом
  Formats/Cso.cs                    CSO/ZSO ↔ ISO
  Formats/Iso9660.cs                ISO 9660: разбор и сборка
  Formats/Img.cs                    IMG/DIR/VER2/скан чанков
  Formats/Dtz.cs                    GAME.DTZ/DAT, модель-инфо, стриминг, .DIR
  Formats/Rsl.cs                    relocatable chunk и Rsl-структуры
  Formats/Chk.cs                    текстуры: палитра, swizzle, DXT, PNG
  Formats/Gxt.cs                    GXT → текст
  Formats/Vag.cs                    PS-ADPCM ↔ WAV
  Formats/FormatId.cs               определение формата по содержимому
  Web/HttpCore.cs                   HTTP/1.1 разбор/ответ
  Web/WebServer.cs                  сокет-сервер, статика, супервизор
  Web/WebApi.cs                     JSON API
public/                             веб-морда (HTML/CSS/ES5)
data/modelnames.lcs.txt             hash → имя модели (LCS)
data/modelnames.vcs.txt             hash → имя модели (VCS)
tools/build_modelnames.py           пересборка этих таблиц из дампов g3DTZ
tools/ci-test.ps1                   всё сразу: сборка, тесты, отчёт
tools/test-formats.ps1              тест форматов на реальных данных (если они есть)
tools/test-web.ps1                  тест HTTP API и статики
docs/                               архитектура, форматы, логи, сессии, API, роадмап
```

## Сборка

```bat
build.cmd
powershell -File tools\ci-test.ps1
```

`build.cmd` собирает `bin\ModKit.exe`, копирует `public\`, `data\` и `config.example.ini`.
Проект открывается в Visual Studio 2010+ (`ModKit.csproj` не нужен, но файлы совместимы).

Совместимость: Windows XP SP3 + .NET 4.0 — целевая среда; Windows 7/10/11 — на .NET 4.8;
браузер — любой современный (ES5, CSS-переменные).

## Атрибуция

- Форматы описаны по публичной документации (gtamods.com wiki) и по наблюдениям на дисках.
- Подход к `GAME.DTZ`/`GAME.DAT` и таблицы имён моделей — из **g3DTZ** (guard3, MIT),
  переписан с C++ на C# как часть этого проекта (`refs/g3DTZ`, в репозиторий не входит).
- Разметка CSO/ZSO — по **cso-to-iso** (CydVilla, MIT).
- Идея пайплайна и веб-хрома — из моего же проекта **DPT.PSP AdHoc Server**.

GTA, Liberty City Stories и Vice City Stories — товарные знаки соответствующих владельцев.