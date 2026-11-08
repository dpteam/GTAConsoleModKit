# Original game images (refs/GAMES)

Что было в архивах, что распаковано, какие образы **идентичны** и что удалено.
Генерируется `tools/import_games.py` и `tools/prune_games.py`; описание — `docs/DATA.md`.

Архивы `.7z/.zip` и промежуточные `.iso/.cso` после проверки распаковки удалены.
SHA-256 образов сохранены здесь, поэтому видно, что именно разбиралось.

## Сводка

| | |
|---|---|
| Записей (архивов/образов) | 25 |
| Уникальных образов (по SHA-256 ISO) | 22 |
| Уникальных наборов данных (по USRDIR) | 19 |
| Оставлено деревьев | 19 |
| Дубликатов данных (деревья удалены) | 6 |

## Распакованные образы

| Источник (удалён) | ISO | DISC_ID | Название | Папка |
|---|---|---|---|---|
| Liberty City Stories (RUS, CSO removed) | 815.1 MB | `ULUS10041` | GTA: Liberty City Stories | `extracted/LCS/` |
| Vice City Stories (RUS, CSO removed) | 1.0 GB | `ULUS10160` | Grand Theft Auto: Vice City Stories | `extracted/VCS/` |
| Grand Theft Auto - Chinatown Wars (Europe) (En,Fr,De,Es,It).7z | 862.3 MB | `ULES01347` | Grand Theft Auto: Chinatown Wars | `extracted/chinatown_wars_europe_en_fr_de_es_it/` |
| Grand Theft Auto - Chinatown Wars (Europe) (En,Fr,De,Es,It) (Beta) (v0.01) (2009-08-31).7z | 771.7 MB | `ULET01349` | Grand Theft Auto: Chinatown Wars | `extracted/chinatown_wars_europe_en_fr_de_es_it_beta_v0_01_2009_08_31/` |
| Grand Theft Auto - Chinatown Wars (Europe) (En,Fr,De,Es,It) (Beta) (v0.02) (2009-09-15).7z | 859.6 MB | `ULET01349` | Grand Theft Auto: Chinatown Wars | `extracted/chinatown_wars_europe_en_fr_de_es_it_beta_v0_02_2009_09_15/` |
| Grand Theft Auto - Chinatown Wars (Japan).7z | 842.7 MB | `ULJM05604` | Grand Theft Auto: Chinatown Wars | `extracted/chinatown_wars_japan/` |
| Chinatown Wars (RUS, CSO removed) | 758.4 MB | `ULUS10490` | Grand Theft Auto: Chinatown Wars | `extracted/chinatown_wars_rus/` |
| Grand Theft Auto - Chinatown Wars (USA).7z | 862.4 MB | `ULUS10490` | Grand Theft Auto: Chinatown Wars | `extracted/chinatown_wars_usa/` |
| Grand Theft Auto - Liberty City Stories (Europe) (En,Fr,De,Es,It) (v1.05).7z | 1.2 GB | `ULES00151` | Grand Theft Auto: Liberty City Stories | `extracted/liberty_city_stories_europe_en_fr_de_es_it_v1_05/` |
| Grand Theft Auto - Liberty City Stories (Europe) (En,Fr,De,Es,It) (v2.00).7z | 1.2 GB | `ULES00151` | Grand Theft Auto: Liberty City Stories | `extracted/liberty_city_stories_europe_en_fr_de_es_it_v2_00/` |
| Grand Theft Auto - Liberty City Stories (Europe) (En,Fr,De,Es,It) (v3.00).7z | 1.2 GB | `ULES00151` | Grand Theft Auto: Liberty City Stories | `extracted/liberty_city_stories_europe_en_fr_de_es_it_v3_00/` |
| Grand Theft Auto - Liberty City Stories (Japan).7z | 1.2 GB | `ULJM05255` | Grand Theft Auto: Liberty City Stories | `extracted/liberty_city_stories_japan/` |
| Grand Theft Auto - Liberty City Stories (Japan) (Rockstar Classics).7z | 1.2 GB | `ULJM05885` | Grand Theft Auto: Liberty City Stories | `extracted/liberty_city_stories_japan_rockstar_classics/` |
| Grand Theft Auto - Liberty City Stories (Korea) (En,Fr,De,Es,It).7z | 1.2 GB | `ULUS10041` | GTA: Liberty City Stories | `extracted/liberty_city_stories_korea_en_fr_de_es_it/` |
| Grand Theft Auto - Liberty City Stories (USA) (v4.00).zip | 1.2 GB | `ULUS10041` | GTA: Liberty City Stories | `extracted/liberty_city_stories_usa_v4_00/` |
| Grand Theft Auto - Vice City Stories (Europe) (En,Fr,De,Es,It).7z | 1.6 GB | `ULES00502` | Grand Theft Auto: Vice City Stories | `extracted/vice_city_stories_europe_en_fr_de_es_it/` |
| Grand Theft Auto - Vice City Stories (Japan).7z | 1.6 GB | `ULJM05297` | Grand Theft Auto: Vice City Stories | `extracted/vice_city_stories_japan/` |
| Grand Theft Auto - Vice City Stories (Japan) (Rockstar Classics).7z | 1.6 GB | `ULJM05884` | Grand Theft Auto: Vice City Stories | `extracted/vice_city_stories_japan_rockstar_classics/` |
| Grand Theft Auto - Vice City Stories (USA).7z | 1.6 GB | `ULUS10160` | Grand Theft Auto: Vice City Stories | `extracted/vice_city_stories_usa/` |

## Идентичные данные

Одинаковый `PSP_GAME/USRDIR` — то есть полностью совпадающие игровые данные
(различаться может только загрузчик `EBOOT.BIN`).

**`12c65d8c1bbf9869…`** — 2 образа, оставлено `extracted/liberty_city_stories_europe_en_fr_de_es_it_v1_05/`:
- Grand Theft Auto - Liberty City Stories (Europe) (En,Fr,De,Es,It) (v1.05).7z (`ULES00151`)
- Grand Theft Auto - Liberty City Stories (USA) (En,Fr,De,Es,It) (v1.05).7z (`ULUS10041`)  ← дерево удалено

**`1ddc259fb4a9b564…`** — 3 образа, оставлено `extracted/liberty_city_stories_europe_en_fr_de_es_it_v2_00/`:
- Grand Theft Auto - Liberty City Stories (Europe) (En,Fr,De,Es,It) (v2.00).7z (`ULES00151`)
- Grand Theft Auto - Liberty City Stories (Germany) (v1.00).7z (`ULES00182`)  ← дерево удалено
- Grand Theft Auto - Liberty City Stories (USA) (En,Fr,De,Es,It) (v2.00).7z (`ULUS10041`)  ← дерево удалено

**`012d0b5c740ce7f7…`** — 2 образа, оставлено `extracted/liberty_city_stories_europe_en_fr_de_es_it_v3_00/`:
- Grand Theft Auto - Liberty City Stories (Europe) (En,Fr,De,Es,It) (v3.00).7z (`ULES00151`)
- Grand Theft Auto - Liberty City Stories (Germany) (v2.00).7z (`ULES00182`)  ← дерево удалено

**`7bf33767674ff7bc…`** — 2 образа, оставлено `extracted/liberty_city_stories_korea_en_fr_de_es_it/`:
- Grand Theft Auto - Liberty City Stories (Korea) (En,Fr,De,Es,It).7z (`ULUS10041`)
- Grand Theft Auto - Liberty City Stories (USA) (En,Fr,De,Es,It) (v3.00).7z (`ULUS10041`)  ← дерево удалено

**`2036c399f8a388bb…`** — 2 образа, оставлено `extracted/vice_city_stories_europe_en_fr_de_es_it/`:
- Grand Theft Auto - Vice City Stories (Europe) (En,Fr,De,Es,It).7z (`ULES00502`)
- Grand Theft Auto - Vice City Stories (Germany).7z (`ULES00503`)  ← дерево удалено

## Идентичные образы (побайтово)

Побайтово одинаковых ISO среди архивов не было — версии отличаются
загрузчиком, но не данными (см. таблицу выше).

## Удалённые дубликаты данных

| Папка | Повторяет |
|---|---|
| `liberty_city_stories_germany_v1_00` | `liberty_city_stories_europe_en_fr_de_es_it_v2_00` |
| `liberty_city_stories_germany_v2_00` | `liberty_city_stories_europe_en_fr_de_es_it_v3_00` |
| `liberty_city_stories_usa_en_fr_de_es_it_v1_05` | `liberty_city_stories_europe_en_fr_de_es_it_v1_05` |
| `liberty_city_stories_usa_en_fr_de_es_it_v2_00` | `liberty_city_stories_europe_en_fr_de_es_it_v2_00` |
| `liberty_city_stories_usa_en_fr_de_es_it_v3_00` | `liberty_city_stories_korea_en_fr_de_es_it` |
| `vice_city_stories_germany` | `vice_city_stories_europe_en_fr_de_es_it` |

## Что где лежит

- `refs/GAMES/extracted/<slug>/PSP_GAME/...` — распакованные данные (это и есть образ).
- `refs/GAMES/extracted/LCS`, `.../VCS`, `.../chinatown_wars_rus` — RUS-переводы.
- `refs/GAMES/MANIFEST.md`, `manifest.json` — этот отчёт.

---

> Снимок отчёта. Обновляется автоматически `tools/import_games.py` и
> `tools/prune_games.py`. Живая версия — `refs/GAMES/MANIFEST.md`
> (папка `refs/` в git не входит).
