# HTTP API

Базовый префикс — `/`. Ответы JSON (`{"ok": true, ...}` либо `{"ok": false, "error": "..."}`
с соответствующим HTTP-кодом). Все ответы отдаются с `X-Content-Type-Options: nosniff`.

## Кто может спрашивать

| Источник | Условие | Что разрешено |
|---|---|---|
| `127.0.0.1` / `::1` | всегда | всё |
| другая машина | `X-DPT-Token: <token>` или `?token=<token>` | `exposure=full` — всё; `exposure=session` — только проекты |
| другая машина | без токена | `401 {auth:"token"}`, кроме `/api/health` |

Токен: `server.token` в `config.ini`, иначе генерируется и хранится в `bin\auth.token`,
печатается в консоль при старте. Морда спрашивает его один раз и держит в `localStorage`
(`dpt.token`), а для `<img>`/`<audio>` подставляет в query, потому что заголовок туда
не передать.

`exposure=session` режет по сети всё, что не про проект:

```
разрешено : GET /api/projects, GET /api/projects/{id}/(files|ops|file|inspect|…),
            POST …/upload, …/note, …/session/*
запрещено : /api/fs, /api/identify, /api/logs, /api/status, POST /api/ops,
            …/folder, …/mkdir, …/delete, DELETE /api/projects/{id}
```

## Служебное

| Метод | Путь | Ответ |
|---|---|---|
| GET | `/api/health` | `{ok, version, uptimeSec, port}` |
| GET | `/api/status` | версия, аптайм, порт, счётчики, проекты, кто онлайн, пути |
| GET | `/api/logs?lines=200` | кольцевой хвост лога (1–500 строк) |
| GET | `/api/identify?path=…` | результат `FormatId` для файла |

## Проекты

| Метод | Путь | Тело/параметры | Ответ |
|---|---|---|---|
| GET | `/api/projects` | — | список проектов |
| POST | `/api/projects` | `{name, game, owner, description}` | `201 {id, code}` |
| GET | `/api/projects/{id}` | — | манифест + файлы + последние операции |
| DELETE | `/api/projects/{id}` | — | `{ok}` (папка удаляется целиком) |
| GET | `/api/projects/{id}/files` | — | список файлов рабочего дерева |
| GET | `/api/projects/{id}/ops?since=N` | — | операции после `N`, `next` |
| POST | `/api/projects/{id}/note` | `{text, user}` | добавленная операция |

## Файлы проекта

| Метод | Путь | Описание |
|---|---|---|
| POST | `/api/projects/{id}/upload?path=relative` | тело — сырые байты, пишется в `files/` |
| GET | `/api/projects/{id}/file?path=` | скачать файл |
| GET | `/api/projects/{id}/inspect?path=` | формат + текстуры/строки/модели |
| GET | `/api/projects/{id}/texture?path=&index=` | PNG конкретной текстуры |
| POST | `/api/projects/{id}/texture-import?path=&index=` | заменить текстуру (тело — PNG) |
| GET | `/api/projects/{id}/gxt-text?path=` | выгрузить текст GXT (редактируемый) |
| POST | `/api/projects/{id}/gxt-import?path=` | собрать GXT из правленого текста |
| GET | `/api/projects/{id}/audio?path=` | WAV из VAG |

`path` — относительный (внутри `files/`) или абсолютный, но только внутри `workspace`
и `gamesRoot`. Иначе `403`.

### Дерево проекта

Рабочее дерево можно собирать не только загрузкой по одному файлу:

| Метод | Путь | Описание |
|---|---|---|
| POST | `/api/projects/{id}/folder` | `{path, as}` — скопировать папку **рекурсивно** в проект |
| POST | `/api/projects/{id}/mkdir` | `{path}` — создать пустую папку |
| POST | `/api/projects/{id}/delete` | `{path}` — убрать файл **или папку целиком** |
| POST | `/api/projects/{id}/build` | `{base, target?, label?, compress?}` — собрать образ: дерево игры + файлы проекта |
| GET | `/api/projects/{id}/img-textures?path=` | все текстуры всех чанков внутри IMG |
| GET | `/api/projects/{id}/img-texture?path=&entry=&index=&w=` | PNG одной текстуры (`w` — ширина превью) |

### Текстуры внутри IMG

`img-textures` отдаёт `{ok, image, format, chunks:[{entry, sector, sectors, textures:[{index,name,width,height,format}]}], textures}`.
Имена чанков берутся из `.DIR`, если он лежит рядом с IMG; иначе IMG сканируется по
заголовкам чанков и чанки называются `tex_000123` / `mdl_000456` — этого достаточно,
чтобы открыть галерею для голого образа из проекта.

`img-texture` рендерит одну текстуру в PNG; `w=96` даёт превью нужной ширины
(nearest-neighbour, чтобы палитровая графика не «мылилась»), `w` опущен — полный размер.
Оба эндпоинта работают через один кэш на файл (`ImgCache`): открытие IMG стоит полного
сканирования, а галерея просит по картинке на плитку.

Замеры на `GTA3PSPHR.IMG` (38.3 МБ): индекс — **3 019 текстур в 582 чанках за 0.52 с**,
первое превью 48 мс, следующие ~34 мс.

Проверка `tools/verify_gallery.py` (эталон — `chk-export` по каждому из 585 CHK):

```
chunks with textures : 582 (extraction: 582)
textures compared    : 3019 (extraction: 3019)
chunks that differ   : 0
chunks without textures: FINGERS.CHK, RADAR04.CHK, SUBGLASS.CHK  (пустые, fileEnd=64)
RESULT: every texture matches byte for byte
```

`tools/verify_gallery_stats.py` дополнительно сверяет размеры PNG с заявленными и
размеры превью: 3 019 из 3 019 — без расхождений.

`build` ставит в очередь задание `build-project` (ответ `202 {job, target, overrides}`),
внутри — тот же `Operations.BuildImage`, что и у CLI: файлы берутся из `base`, а копии
из `files/` проекта их заменяют, дерево не копируется. Пустой `target` = папка
`export/` проекта, имя `<id>.iso` или `<id>.cso`.

`folder.path` может быть абсолютным (внутри `workspace`/`gamesRoot`) или относительным:
сначала он ищется в папке проекта, потом в `gamesRoot` — так «добавить папку из дерева
игры» работает без копирования путей вручную. `as` задаёт имя папки в проекте,
по умолчанию берётся имя исходной.

Ответ `folder`: `{ok, folder, files, folders, bytes}` — сколько реально скопировано.
`delete` возвращает `{ok, path, folder}` и пишет операцию в журнал (`kind=delete`),
поэтому в совместной сессии видно, кто что убрал.

Защита: путь вне корней и `..` — `403`; удаление корня проекта (`path` пустой или `.`)
— `400`; всё остальное — `404`.

В веб-морде это дерево: папки раскрываются, у каждой строки есть «×» (убрать из проекта),
в шапке — «+ Папка», «+ С ПК» и «+ Файлы».

### Откуда берётся папка: сервер или клиент

Браузер на другом компьютере **не может** отдать серверу свой путь: диск `T:` у клиента
для сервера не существует, и никакая проверка прав тут не поможет — файла просто нет
в его файловой системе. Поэтому путей два:

| Кнопка | Что делает | Когда нужна |
|---|---|---|
| «+ Папка» | проводник по файлам **сервера**, копирование рекурсивно | дампы лежат рядом с сервером (`refs\…`), общая шара |
| «+ С ПК» | браузер читает папку у себя (`webkitdirectory`) и заливает файлы по одному | файлы есть только на твоей машине |

Проводник ходит по тем же корням, что и API, запоминает последний путь в
`localStorage` (`dpt.addFolder.last`) и не даёт выйти за пределы корней.

```
GET /api/fs              {roots: [{path, name}], folders: [], path: ""}
GET /api/fs?path=...     {path, parent, folders: [{name, path, files, folders}], files}
                         403, если путь вне корней (в тексте — список корней)
```

### Корни, из которых можно читать

`workspace` и `gamesRoot` разрешены всегда; `workspace.roots` в `config.ini` добавляет
остальные (`;` или `,`). Если не задано ничего, разрешена папка набора целиком (`refs\`
внутри неё), поэтому «добавить папку» работает сразу после распаковки.

Сравнение путей не строковое: сначала нормализация, потом сверка **идентичности файла**
(серийник тома + индекс). Поэтому `X:\refs` (subst), junction, 8.3-имя и сетевой share,
которые ведут в тот же каталог, считаются тем же каталогом. Если сервер этот путь не
видит (диск примонтирован только у клиента), ответ будет `403` со списком корней.

## Сессии

| Метод | Путь | Описание |
|---|---|---|
| GET | `/api/projects/{id}/session` | код, участники, `activity` |
| POST | `/api/projects/{id}/session/join` | `{nick, clientId}` |
| POST | `/api/projects/{id}/session/leave` | `{clientId}` |
| GET | `/api/projects/{id}/session/wait?activity=&clientId=&ms=` | long-poll |

## Задания и операции

| Метод | Путь | Описание |
|---|---|---|
| GET | `/api/jobs?count=40` | список заданий |
| GET | `/api/jobs/{jobId}` | состояние и результат |
| POST | `/api/jobs/{jobId}/cancel` | кооперативная отмена |
| POST | `/api/ops` | запуск операции, `202` + `job` |

`POST /api/ops`:

```json
{ "name": "chk-export", "project": "lcs_patch",
  "source": "textures/LOADSC0.CHK", "target": "png_out",
  "extra": "", "user": "DartPower" }
```

Имена операций: `cso-to-iso`, `iso-to-cso`, `iso-extract`, `iso-build`, `img-list`,
`img-extract`, `img-replace`, `img-pack`, `img-repack`, `dtz-inspect`, `dtz-dat`, `dtz-dtz`, `dtz-dir`, `dtz-models`,
`chk-list`, `chk-export`, `gxt-dump`, `gxt-import`, `vag-to-wav`, `wav-to-vag`.

`source` обязателен. `target` для операций, которые пишут результат, — абсолютный
(внутри разрешённых корней) или относительный, тогда он считается **от `export/`
проекта**. `extra` — DIR-файл для IMG, метка тома для `iso-build`.

## Статика

`/` отдаёт `public/index.html`; `/assets/...` — статика с `Cache-Control` (js/css — 
`no-cache`, картинки — 10 минут). Неизвестный путь без точки отдаёт `index.html`
(одностраничный fallback).

## Коды ответов

`200` ok, `201` создано, `202` принято в очередь, `400` плохие параметры,
`403` путь вне разрешённых корней, `404` нет такого проекта/файла/эндпоинта,
`405` метод не тот, `413` тело больше лимита, `500` внутренняя ошибка (в `error` —
короткое описание, полный стек — в файле лога).

## Пример (PowerShell)

```powershell
$b = "http://127.0.0.1:44700"
Invoke-RestMethod "$b/api/projects" -Method Post -ContentType application/json `
  -Body (@{name="LCS patch"; game="LCS"; owner="me"} | ConvertTo-Json)

Invoke-RestMethod "$b/api/ops" -Method Post -ContentType application/json `
  -Body (@{name="dtz-dir"; project="lcs_patch";
           source="PSP_GAME/USRDIR/CHK/PSPHR/GAME.DTZ"; target="lcs.dir"} | ConvertTo-Json)

Invoke-RestMethod "$b/api/projects/lcs_patch/ops"
```