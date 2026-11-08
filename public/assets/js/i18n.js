/* DPT.PSP GTA Modding Kit - i18n (ru/en), ES5, no dependencies */
(function (global) {
  'use strict';

  var DICT = {
    en: {
      'nav.projects': 'Projects',
      'nav.logs': 'Log',
      'nav.about': 'About',
      'stat.projects': 'Projects',
      'stat.online': 'Online',
      'stat.online-sub': 'in sessions',
      'stat.jobs': 'Jobs',
      'stat.uptime': 'Uptime',
      'projects.title': 'Projects',
      'projects.search': 'Search by name or code',
      'projects.empty': 'No projects yet. Create one - a project is a folder inside workspace.',
      'projects.new': 'New project',
      'projects.name': 'Name',
      'projects.name-ph': 'e.g. LCS RUS patch',
      'projects.game': 'Game',
      'projects.author': 'Author',
      'projects.desc': 'Description',
      'projects.desc-ph': 'What are we building',
      'projects.create': 'Create project',
      'projects.hint': 'Every project gets a session code: a colleague uses it to join the same work.',
      'jobs.title': 'Jobs',
      'jobs.empty': 'No jobs yet.',
      'files.title': 'Files',
      'files.add': '+ Files',
      'files.uploadFolder': '+ From PC',
      'files.uploadFolderTitle': 'Upload a folder from this computer (browser reads it and sends the files)',
      'files.uploadTitle': 'Upload files from this computer',
      'files.addFolder': '+ Folder',
      'files.addFolderTitle': 'Add a folder that the server can see, recursively',
      'files.pickRoot': 'server folders',
      'files.pickThis': 'Add this folder',
      'files.noSubfolders': 'No subfolders here.',
      'files.noRoots': 'No roots configured (see workspace.roots in config.ini).',
      'files.pickFirst': 'Pick a folder first',
      'files.shortCount': '{0}',
      'files.shortFolders': '{0} dir',
      'files.addFolderPh': 'Folder path, e.g. E:\\Games\\LCS\\PSP_GAME',
      'files.addFolderAsPh': 'Name in the project (optional)',
      'files.addFolderRun': 'Add folder',
      'files.newFolderRun': 'Empty folder',
      'files.cancel': 'Cancel',
      'files.delete': 'Remove from the project',
      'files.confirmDelete': 'Remove {0} from the project?',
      'files.deleted': 'Removed {0}',
      'files.added': '{0}: {1} file(s), {2}',
      'files.created': 'Folder {0} created',
      'files.nameRequired': 'Give the folder a name',
      'files.count': '{0} file(s), {1}',
      'files.empty': 'Empty. Upload files, add a folder or extract an ISO into the project folder.',
      'inspect.title': 'Inspector',
      'inspect.empty': 'Pick a file on the left to see format, textures, text or models.',
      'ptab.tools': 'Operations',
      'ptab.session': 'Session',
      'ptab.log': 'History',
      'ops.title': 'Quick operations',
      'ops.hint': 'Paths may be absolute (inside workspace or gamesRoot) or relative to the project folder.',
      'ops.identify': 'Identify file',
      'ops.path': 'Path',
      'ops.identify-run': 'Identify',
      'session.title': 'Shared session',
      'session.sub': 'the project code connects two people to one job',
      'session.nick': 'Nickname',
      'session.join': 'Join',
      'session.leave': 'Leave',
      'session.note': 'Note to the history',
      'session.note-ph': 'Done / next step',
      'session.send': 'Add',
      'session.code': 'Session code',
      'session.code-hint': 'Give the code to a colleague: they open the same server, then join the live session.',
      'session.mode': 'Mode',
      'log.title': 'Operation history',
      'logs.title': 'Server log',
      'logs.follow': 'Follow',
      'about.title': 'DPT.PSP GTA Modding Kit',
      'about.formats': 'Formats',
      'toast.created': 'Project created: {0}',
      'toast.failed': 'Failed: {0}',
      'toast.jobQueued': 'Job {0} queued',
      'toast.jobDone': 'Job finished: {0}',
      'toast.jobFailed': 'Job failed: {0}',
      'toast.uploaded': 'Uploaded {0}',
      'toast.uploadingMany': 'Uploading {0} file(s)...',
      'toast.uploadedMany': 'Uploaded {0} of {1} file(s)',
      'auth.need': 'This server needs a token for requests from other machines.',
      'auth.ph': 'token from the server console (also in bin\\auth.token)',
      'auth.save': 'Sign in',
      'auth.saved': 'Token saved, reloading',
      'auth.sessionOnly': 'Server is in session-only mode: only project collaboration is allowed from the network.',
      'build.title': 'Build an image from the project',
      'build.base': 'Game tree (with PSP_GAME inside)',
      'build.target': 'Where to write (empty = project export folder)',
      'build.cso': 'Straight to CSO',
      'build.label': 'Volume label',
      'build.run': 'Build',
      'build.hint': 'The image takes files from the game tree; project files replace their copies, so the tree is never copied.',
      'build.summary': '{0} file(s), {1} will override the base tree',
      'build.empty': 'Project is empty: the image would be a copy of the base tree',
      'build.started': 'Building in the background, {0} override(s)',
      'toast.joined': 'Joined the session',
      'toast.left': 'Left the session',
      'toast.noteAdded': 'Note added',
      'toast.texReplaced': 'Texture replaced: {0}',
      'toast.gxtImported': 'Text imported, {0} strings changed',
      'toast.identified': 'Identified',
      'toast.needPath': 'Path is required',
      'toast.needNick': 'Enter a nickname',
      'inspector.none': 'no data',
      'inspector.textures': 'Textures',
      'tex.import': 'Replace this texture with a PNG',
      'gxt.download': 'Download text',
      'gxt.upload': 'Import edited text',
      'inspector.strings': 'Strings',
      'inspector.models': 'Model infos',
      'session.offline': 'offline',
      'session.online': 'online',
      'job.running': 'running',
      'job.queued': 'queued',
      'job.done': 'done',
      'job.failed': 'failed',
      'job.cancelled': 'cancelled',
      'about.intro': 'Toolkit for GTA Liberty City Stories and Vice City Stories (PSP). .NET 4.0, no dependencies, web UI plus a headless CLI.'
    },

    ru: {
      'nav.projects': 'Проекты',
      'nav.logs': 'Лог',
      'nav.about': 'О наборе',
      'stat.projects': 'Проектов',
      'stat.online': 'Онлайн',
      'stat.online-sub': 'в сессиях',
      'stat.jobs': 'Задания',
      'stat.uptime': 'Аптайм',
      'projects.title': 'Проекты',
      'projects.search': 'Поиск по названию или коду',
      'projects.empty': 'Пока нет проектов. Создайте первый — это одна папка в workspace.',
      'projects.new': 'Новый проект',
      'projects.name': 'Название',
      'projects.name-ph': 'Например: LCS RUS patch',
      'projects.game': 'Игра',
      'projects.author': 'Автор',
      'projects.desc': 'Описание',
      'projects.desc-ph': 'Что делаем',
      'projects.create': 'Создать проект',
      'projects.hint': 'Каждому проекту выдаётся код сессии: по нему подключается второй человек.',
      'jobs.title': 'Задания',
      'jobs.empty': 'Заданий ещё не было.',
      'files.title': 'Файлы',
      'files.add': '+ Файлы',
      'files.uploadFolder': '+ С ПК',
      'files.uploadFolderTitle': 'Загрузить папку с этого компьютера (браузер прочитает её и передаст файлы)',
      'files.uploadTitle': 'Загрузить файлы с этого компьютера',
      'files.addFolder': '+ Папка',
      'files.addFolderTitle': 'Добавить папку, которую видит сервер, рекурсивно',
      'files.pickRoot': 'папки сервера',
      'files.pickThis': 'Добавить эту папку',
      'files.noSubfolders': 'Подпапок нет.',
      'files.noRoots': 'Корни не заданы (см. workspace.roots в config.ini).',
      'files.pickFirst': 'Сначала выберите папку',
      'files.shortCount': '{0}',
      'files.shortFolders': '{0} пап',
      'files.addFolderPh': 'Путь к папке, например E:\\Games\\LCS\\PSP_GAME',
      'files.addFolderAsPh': 'Имя в проекте (необязательно)',
      'files.addFolderRun': 'Добавить папку',
      'files.newFolderRun': 'Пустая папка',
      'files.cancel': 'Отмена',
      'files.delete': 'Убрать из проекта',
      'files.confirmDelete': 'Убрать {0} из проекта?',
      'files.deleted': 'Убрано: {0}',
      'files.added': '{0}: {1} файл(ов), {2}',
      'files.created': 'Папка {0} создана',
      'files.nameRequired': 'Укажите имя папки',
      'files.count': 'Файлов: {0}, {1}',
      'files.empty': 'Пусто. Загрузите файлы, добавьте папку или распакуйте ISO в папку проекта.',
      'inspect.title': 'Инспектор',
      'inspect.empty': 'Выберите файл слева, чтобы увидеть формат, текстуры, строки или модели.',
      'ptab.tools': 'Операции',
      'ptab.session': 'Сессия',
      'ptab.log': 'Журнал',
      'ops.title': 'Быстрые операции',
      'ops.hint': 'Пути можно указывать абсолютные (внутри workspace и gamesRoot) или относительные — от папки проекта.',
      'ops.identify': 'Определить файл',
      'ops.path': 'Путь',
      'ops.identify-run': 'Определить',
      'session.title': 'Совместная сессия',
      'session.sub': 'код проекта соединяет двух людей в одну работу',
      'session.nick': 'Ник',
      'session.join': 'Подключиться',
      'session.leave': 'Выйти',
      'session.note': 'Заметка в журнал',
      'session.note-ph': 'Что сделано / что дальше',
      'session.send': 'Добавить',
      'session.code': 'Код сессии',
      'session.code-hint': 'Передайте код коллеге: он открывает проект с этого же сервера и подключается к живой сессии.',
      'session.mode': 'Режим',
      'log.title': 'Журнал операций',
      'logs.title': 'Лог сервера',
      'logs.follow': 'Следить',
      'about.title': 'DPT.PSP GTA Modding Kit',
      'about.formats': 'Форматы',
      'toast.created': 'Проект создан: {0}',
      'toast.failed': 'Ошибка: {0}',
      'toast.jobQueued': 'Задание {0} в очереди',
      'toast.jobDone': 'Задание выполнено: {0}',
      'toast.jobFailed': 'Задание упало: {0}',
      'toast.uploaded': 'Загружено {0}',
      'toast.uploadingMany': 'Загружаю файлов: {0}...',
      'toast.uploadedMany': 'Загружено {0} из {1} файл(ов)',
      'auth.need': 'Этот сервер требует токен для доступа с других машин.',
      'auth.ph': 'токен из консоли сервера (есть в bin\\auth.token)',
      'auth.save': 'Войти',
      'auth.saved': 'Токен сохранён, обновляю',
      'auth.sessionOnly': 'Сервер в режиме «только сессия»: по сети доступна лишь совместная работа с проектами.',
      'build.title': 'Собрать образ из проекта',
      'build.base': 'Дерево игры (внутри PSP_GAME)',
      'build.target': 'Куда писать (пусто = папка export проекта)',
      'build.cso': 'Сразу в CSO',
      'build.label': 'Метка тома',
      'build.run': 'Собрать',
      'build.hint': 'В образ идут файлы дерева игры, а файлы проекта заменяют свои копии — дерево не копируется.',
      'build.summary': 'Файлов: {0}, {1} заменят дерево',
      'build.empty': 'Проект пуст: образ будет копией дерева',
      'build.started': 'Сборка в фоне, замен: {0}',
      'toast.joined': 'Вы в сессии',
      'toast.left': 'Вы вышли из сессии',
      'toast.noteAdded': 'Заметка добавлена',
      'toast.texReplaced': 'Текстура заменена: {0}',
      'toast.gxtImported': 'Текст импортирован, изменено строк: {0}',
      'toast.identified': 'Формат определён',
      'toast.needPath': 'Нужен путь',
      'toast.needNick': 'Введите ник',
      'inspector.none': 'нет данных',
      'inspector.textures': 'Текстуры',
      'tex.import': 'Заменить текстуру PNG-файлом',
      'gxt.download': 'Скачать текст',
      'gxt.upload': 'Импорт правленого текста',
      'inspector.strings': 'Строки',
      'inspector.models': 'Модели',
      'session.offline': 'оффлайн',
      'session.online': 'онлайн',
      'job.running': 'выполняется',
      'job.queued': 'в очереди',
      'job.done': 'готово',
      'job.failed': 'ошибка',
      'job.cancelled': 'отменено',
      'about.intro': 'Инструментарий для GTA Liberty City Stories и Vice City Stories (PSP). .NET 4.0, без зависимостей, веб-морда и headless CLI.'
    }
  };

  var LANGS = [
    { code: 'ru', label: 'Русский' },
    { code: 'en', label: 'English' }
  ];

  var current = 'ru';

  function detect() {
    var saved = null;
    try { saved = localStorage.getItem('dpt.lang'); } catch (e) { }
    if (saved && DICT[saved]) return saved;
    var nav = (navigator.language || navigator.userLanguage || 'en').toLowerCase();
    if (nav.indexOf('ru') === 0) return 'ru';
    return 'en';
  }

  function t(key) {
    var table = DICT[current] || DICT.en;
    var text = table[key];
    if (text === undefined) text = DICT.en[key];
    if (text === undefined) return key;
    for (var i = 1; i < arguments.length; i++) {
      text = text.replace('{' + (i - 1) + '}', arguments[i]);
    }
    return text;
  }

  function apply(root) {
    var scope = root || document;
    var nodes = scope.querySelectorAll('[data-i18n]');
    for (var i = 0; i < nodes.length; i++) {
      var key = nodes[i].getAttribute('data-i18n');
      nodes[i].textContent = t(key);
    }
    nodes = scope.querySelectorAll('[data-i18n-ph]');
    for (var j = 0; j < nodes.length; j++) {
      nodes[j].setAttribute('placeholder', t(nodes[j].getAttribute('data-i18n-ph')));
    }
    nodes = scope.querySelectorAll('[data-i18n-title]');
    for (var k = 0; k < nodes.length; k++) {
      nodes[k].setAttribute('title', t(nodes[k].getAttribute('data-i18n-title')));
    }
    document.documentElement.lang = current;
  }

  function setLang(code) {
    if (!DICT[code]) return;
    current = code;
    try { localStorage.setItem('dpt.lang', code); } catch (e) { }
    apply(document);
    if (global.App && global.App.onLangChanged) global.App.onLangChanged();
  }

  function fillSelect(select) {
    select.innerHTML = '';
    for (var i = 0; i < LANGS.length; i++) {
      var option = document.createElement('option');
      option.value = LANGS[i].code;
      option.textContent = LANGS[i].label;
      if (LANGS[i].code === current) option.selected = true;
      select.appendChild(option);
    }
  }

  global.I18N = {
    t: t,
    apply: apply,
    setLang: setLang,
    fillSelect: fillSelect,
    detect: detect,
    get lang() { return current; },
    init: function () {
      current = detect();
      apply(document);
    }
  };
})(window);