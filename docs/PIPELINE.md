# Пайплайн: от текстуры до играбельного образа

Короткий путь, который проходит мод: `текстура → CHK → IMG → ISO/CSO`.
Каждый шаг — отдельная операция, поэтому можно остановиться на любом.

## Что нужно

- распакованное дерево игры (`PSP_GAME/...`), например `refs/GAMES/extracted/LCS`
- проект (`workspace/<id>/files/`), в нём правки в той же структуре, что и в образе
  (`PSP_GAME/USRDIR/MODELS/GTA3PSPHR.IMG`)

## Шаги

```bat
:: 1. каталог IMG из таблицы стриминга GAME.DAT
ModKit.exe dtz-dir  LCS\PSP_GAME\USRDIR\CHK\PSPHR\GAME.DTZ  lcs.dir

:: 2. разложить IMG на файлы (имена из .DIR, ничего не угадываем)
ModKit.exe img-extract LCS\PSP_GAME\USRDIR\MODELS\GTA3PSPHR.IMG  work  --dir lcs.dir

:: 3. текстуры: наружу и обратно
ModKit.exe chk-export  work\PLAYER.CHK  png
ModKit.exe chk-replace work\PLAYER.CHK  png\toni_head128_X_8bit.png  0

:: 4. вернуть чанк в IMG (на месте, без сдвига остальных записей)
ModKit.exe img-replace work\GTA3PSPHR.IMG  PLAYER.CHK  work\PLAYER.CHK  --dir lcs.dir

:: 5. собрать играбельный образ: дерево игры + папка правок
ModKit.exe build-image LCS  overrides  MOD.iso   --label DPT_MOD
::    ...или сразу сжатый:
ModKit.exe build-image LCS  overrides  MOD.cso   --cso
```

Папка `overrides` — это содержимое проекта, то есть пути **относительно корня ISO**:

```
overrides\
  PSP_GAME\USRDIR\MODELS\GTA3PSPHR.IMG
  PSP_GAME\USRDIR\TEXT\ENGLISH.GXT
  PSP_GAME\USRDIR\TXD\LOADSC0.CHK
```

Фишка `build-image`: дерево игры **не копируется**. При сборке ISO файлы берутся из дерева,
а перечисленные в `overrides` — из проекта (и их размеры тоже, поэтому файл может
вырасти). Для гигабайтной игры это экономит и время, и место.

## Проверено на реальных данных

Полный прогон на LCS (RUS):

| Шаг | Результат |
|---|---|
| `dtz-dir` | 1121 запись (536 моделей + 585 текстур) |
| `img-extract` | 1121 файл, имена `PLAYER.MDL`, `COP.CHK`, … |
| `chk-replace` | `PLAYER.CHK` изменён, палитра сохранена |
| `img-replace` | IMG остался 40 169 472 байта (замена влезла в те же 23 сектора) |
| `build-image` | ISO 854 743 040 байт, 246 файлов / 27 каталогов (7-Zip читает) |
| проверка | IMG внутри ISO **побайтово равен** пропатченному, `PLAYER.CHK` внутри — правленый, исходное дерево не тронуто |

## Тексты

```bat
ModKit.exe gxt-dump   LCS\PSP_GAME\USRDIR\TEXT\ENGLISH.GXT  en.txt
:: правим строки в любом редакторе
ModKit.exe gxt-import LCS\PSP_GAME\USRDIR\TEXT\ENGLISH.GXT  en.txt
```

Импорт без правок даёт **байт в байт** исходный файл, поэтому переводчик сразу видит,
не сломал ли он чего (0 отличий в `ENGLISH.GXT`, 526 264 байта).

## Если правка не влезает в IMG

`img-replace` откажет с объяснением: рост записи сдвинул бы все следующие секторы и
порвал бы стриминг. Варианты:

- `img-repack` — допишет выросшую запись в конец и поправит её слот в `GAME.DAT`
  (используется, когда правка пришла из IMG, а не из папки);
- если правок много и раскладка меняется сильно — `build-image` соберёт образ из
  подготовленного дерева целиком.

## Что пока не влезает в пайплайн

- районные IMG (`COMMER`, `INDUST`, …) — их каталог не разобран (см. `docs/FORMATS.md` §3.3),
  поэтому модели районов пока не редактируются;
- геометрия MDL — читаются границы чанка, импорт/экспорт не сделан;
- PS2-версии — `GAME.DTZ` определяется, но не индексируется.