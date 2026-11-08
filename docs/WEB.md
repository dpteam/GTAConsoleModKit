# HTTP API

Базовый префикс — `/`. Ответы JSON (`{"ok": true, ...}` либо `{"ok": false, "error": "..."}`
с соответствующим HTTP-кодом). Все ответы отдаются с `X-Content-Type-Options: nosniff`.

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
| GET | `/api/projects/{id}/audio?path=` | WAV из VAG |

`path` — относительный (внутри `files/`) или абсолютный, но только внутри `workspace`
и `gamesRoot`. Иначе `403`.

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