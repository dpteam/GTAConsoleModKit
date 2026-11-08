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
| `.ISO` (ISO 9660 level 2) | да | да: сборка с нуля и поверх дерева игры с правками |
| `.IMG` (Stories, `.DIR` + сектора) | да | да (замена на месте, перенос выросших + правка стриминга) |
| `.IMG` (GTA III/VC `.DIR`, SA `VER2`) | да | да |
| `GAME.DTZ` ↔ `GAME.DAT` | да | да (zlib) |
| Таблица стриминга `GAME.DAT` → `.DIR` | да, LCS и VCS (сверено с чанками IMG) | экспорт DIR |
| `.LVZ` (zlib + чанк `WRLD`) | да | пересжатие |
| `.CHK` / `.XTX` (текстуры) | да: список, палитра, swizzle, mip, PNG | да: PNG → текстура (pal4/pal8) |
| текстуры всего IMG (`tex-*`) | да: индекс, пакетный экспорт в PNG + `index.txt` | да: пакетный импорт на место |
| `.MDL`, `.ANIM` (чанки `ldm`/`anim`) | заголовок и границы | — |
| `.GXT` (таблицы текста) | да, дамп в редактируемый текст | да (UTF-16, сортировка ключей) |
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
ModKit.exe build-image <tree> <overrides> <out.iso|.cso> [--cso] [--label X]
ModKit.exe tex-index <image.img> [--dir x.dir]
ModKit.exe tex-export-all <image.img> <folder> [--dir x.dir]
ModKit.exe tex-import-all <image.img> <folder> [--dir x.dir] [--all]
ModKit.exe img-list <image.img> [dir]
ModKit.exe img-extract <img> [folder] [--dir <dir>]
ModKit.exe img-replace <img> <entry> <file> [--dir <dir>]
ModKit.exe img-pack <folder> [out.img] [--ext .mdl]
ModKit.exe img-repack <img> <GAME.DTZ> [--folder dir] [--out new.img] [--out-dtz new.dtz]
ModKit.exe dtz-inspect <GAME.DTZ|GAME.DAT>
ModKit.exe dtz-dat <GAME.DTZ> [out.dat]
ModKit.exe dtz-dtz <GAME.DAT> [out.dtz]
ModKit.exe dtz-dir <GAME.DTZ> [out.dir] [--vcs]
ModKit.exe dtz-models <GAME.DTZ> [--vcs] [--limit N]
ModKit.exe chk-list <file.chk|.xtx>
ModKit.exe chk-export <file.chk> [folder]
ModKit.exe chk-replace <chk> <png> [index] [out.chk]
ModKit.exe gxt-dump <file.gxt> [out.txt]
ModKit.exe gxt-import <file.gxt> <edited.txt> [out.gxt]
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

По умолчанию сервер слушает `0.0.0.0:44700` — морда открывается с телефона и второго
компьютера по `http://<LAN-IP>:44700/`. Чтобы Windows пропустил входящие, один раз
запустите `tools\firewall.cmd` (появится UAC):

| Правило | Порт | Источники |
|---|---|---|
| `DPT.PSP ModKit` | 44700 (+ fallback из `config.ini`) | LAN/приватные адреса |

Открыли `IMG` в инспекторе — видна **галерея всех текстур** внутри образа: поиск по
имени, превью, клик открывает оригинал. На розничном LCS это 3 019 текстур в 582 словарях.

Одна кнопка на выходе: «Операции» → **«Собрать образ из проекта»** — берёт дерево
игры, подставляет файлы проекта и пишет ISO/CSO в `export/` проекта (или по указанному
пути). В журнал попадает запись `build`.

Проект собирается деревом: «+ Папка» открывает проводник по файлам **сервера**
(запоминает последний путь), «+ С ПК» заливает папку прямо с твоего компьютера,
«×» у строки убирает файл или папку. Читать можно только из `workspace`, `gamesRoot`
и того, что перечислено в `workspace.roots` в `config.ini`.

Опции: `-AnySource` (разрешить с любых адресов), `-Remove` (удалить правило).
Скрипт печатает локальные IPv4, состояние сокетов и проверку `/api/health`.

Хотите вернуть «только этот компьютер» — `bind=127.0.0.1` в `config.ini`.

### Доступ с других машин

С `0.0.0.0` сервер пускает по **токену** (как в AdHoc): он печатается при старте и
лежит в `bin\auth.token`, морда спрашивает его один раз и хранит в браузере.
С `127.0.0.1` токен не нужен никогда.

| `exposure` | Что может держатель токена |
|---|---|
| `full` (по умолчанию) | всё, что умеет набор: произвольные пути внутри корней, операции, сборка образов |
| `session` | только работа внутри проектов: файлы, журнал, заметки, загрузка. Без `/api/fs`, без запуска операций |

`session` — это режим для выставления наружу (Nginx Proxy Manager): сервер превращается
в «доску совместной работы», а файловая система и сборка остаются локальными.

Мелочи: `/api/health` открыт всем (мониторинг), сравнение токена — без утечки по времени,
токен меняется удалением `bin\auth.token` и перезапуском.

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
tools/import_games.py               разбор архивов с образами (refs/GAMES)
tools/prune_games.py                отпечатки данных, удаление дублей и мусора
tools/render_manifest.py            перерисовать MANIFEST.md и снимок docs/GAME_IMAGES.md
tools/firewall.cmd / .ps1           правило Windows Firewall (UAC) для морды
tools/ci-test.ps1                   всё сразу: сборка, тесты, отчёт
tools/test-formats.ps1              тест форматов на реальных данных (если они есть)
tools/test-web.ps1                  тест HTTP API и статики
docs/PIPELINE.md                    от текстуры до играбельного образа (с цифрами)
docs/DATA.md                        как устроены данные в refs/ и как их пересобрать
docs/GAME_IMAGES.md                 какие образы идентичны (снимок отчёта)
docs/                               архитектура, форматы, логи, сессии, API, роадмап
```

## Непрерывная сборка (GitHub Actions)

`.github/workflows/build.yml` — то же, что локальный `tools\ci-test.ps1`, но на
`windows-latest` и со стоковым `csc.exe` (никаких NuGet и MSBuild):

| Шаг | Что делает |
|---|---|
| Toolchain | проверяет наличие `csc.exe` из .NET Framework 4.x |
| Build and test | `tools\ci-test.ps1` — сборка + тесты форматов и веб-API |
| Package | собирает zip: `ModKit.exe`, `public\`, `data\`, `config.example.ini`, `run.cmd`, `README`, `LICENSE` |
| Upload artifact | `DPT.PSPModKit-win.zip` (и `logs` при падении) |
| Release | по тегу `v*` прикладывает zip к релизу через `gh` |

Тесты на реальных данных (LCS/VCS) в CI не запускаются: `refs/` в git не входит,
поэтому проверяется самодостаточная часть набора форматов и весь веб-набор.
Артефакт — готовый к запуску набор: распаковать и `run.cmd`.

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