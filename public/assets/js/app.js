/* DPT.PSP GTA Modding Kit - application shell (ES5, no frameworks) */
(function (global) {
  'use strict';

  var t = global.I18N.t;

  var state = {
    view: 'dash',
    projectId: null,
    project: null,
    files: [],
    selectedFile: null,
    status: null,
    jobs: [],
    activeJobs: {},
    session: null,
    clientId: null,
    nick: '',
    activity: 0,
    waiting: false,
    opsRenderCount: 0,
    logsFollow: true,
    filter: ''
  };

  var OP_DEFS = [
    { name: 'cso-to-iso', title: 'CSO → ISO', desc: 'Распаковать сжатый образ PSP.', source: 'CSO файл', target: 'ISO (необязательно)' },
    { name: 'iso-to-cso', title: 'ISO → CSO', desc: 'Сжать образ обратно в CSO (deflate, 2048).', source: 'ISO файл', target: 'CSO (необязательно)' },
    { name: 'iso-extract', title: 'Распаковать ISO', desc: 'Вытащить дерево файлов из образа.', source: 'ISO файл', target: 'Папка' },
    { name: 'iso-build', title: 'Собрать ISO', desc: 'Собрать образ из папки (ISO 9660 level 2).', source: 'Папка', target: 'ISO файл', extra: 'Метка тома' },
    { name: 'img-list', title: 'IMG: список', desc: 'Показать записи IMG (использует .DIR, если он есть).', source: 'IMG файл', extra: 'DIR файл (необязательно)' },
    { name: 'img-extract', title: 'IMG: распаковать', desc: 'Достать записи IMG в папку.', source: 'IMG файл', target: 'Папка', extra: 'DIR файл' },
    { name: 'dtz-inspect', title: 'DTZ: инспект', desc: 'Таблица стриминга GAME.DAT.', source: 'GAME.DTZ' },
    { name: 'dtz-dir', title: 'DTZ → DIR', desc: 'Экспорт .DIR для соответствующего IMG.', source: 'GAME.DTZ', target: 'DIR файл' },
    { name: 'chk-list', title: 'CHK: список', desc: 'Текстуры внутри CHK/XTX.', source: 'CHK файл' },
    { name: 'chk-export', title: 'CHK → PNG', desc: 'Разложить все текстуры в PNG.', source: 'CHK файл', target: 'Папка' },
    { name: 'gxt-dump', title: 'GXT → TXT', desc: 'Дамп строк в текстовый файл.', source: 'GXT файл', target: 'TXT файл' },
    { name: 'vag-to-wav', title: 'VAG → WAV', desc: 'Декодировать PS-ADPCM.', source: 'VAG файл', target: 'WAV файл' }
  ];

  // ------------------------------------------------------------------ utils

  function $(id) { return document.getElementById(id); }

  function el(tag, className, text) {
    var node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined && text !== null) node.textContent = text;
    return node;
  }

  function clear(node) {
    while (node.firstChild) node.removeChild(node.firstChild);
  }

  function fmtSize(bytes) {
    if (bytes === undefined || bytes === null) return '';
    if (bytes < 1024) return bytes + ' B';
    var units = ['KB', 'MB', 'GB'], value = bytes, i = -1;
    do { value /= 1024; i++; } while (value >= 1024 && i < units.length - 1);
    return (value < 10 ? value.toFixed(1) : Math.round(value)) + ' ' + units[i];
  }

  function fmtUptime(seconds) {
    if (!seconds) return '—';
    var h = Math.floor(seconds / 3600), m = Math.floor((seconds % 3600) / 60);
    if (h > 0) return h + ' ч ' + m + ' мин';
    if (m > 0) return m + ' мин';
    return Math.round(seconds) + ' с';
  }

  function initials(name) {
    if (!name) return '?';
    return name.substr(0, 2).toUpperCase();
  }

  function toast(message, kind) {
    var box = $('toasts');
    var node = el('div', 'toast ' + (kind || ''), message);
    box.appendChild(node);
    setTimeout(function () {
      if (node.parentNode) {
        node.style.opacity = '0';
        setTimeout(function () { if (node.parentNode) box.removeChild(node); }, 250);
      }
    }, 4200);
  }

  function fail(error) {
    toast(t('toast.failed', error && error.message ? error.message : String(error)), 'err');
    global.Sound.play('error');
  }

  // ------------------------------------------------------------------- nav

  function showView(name) {
    state.view = name;
    var views = document.querySelectorAll('.view');
    for (var i = 0; i < views.length; i++) views[i].classList.add('hidden');
    $('view-' + name).classList.remove('hidden');
    var tabs = document.querySelectorAll('#tabs .tab');
    for (var j = 0; j < tabs.length; j++) {
      tabs[j].classList.toggle('active', tabs[j].getAttribute('data-view') === name);
    }
    if (name === 'logs') refreshLogs();
    if (name === 'about') renderAbout();
  }

  // -------------------------------------------------------------- dashboard

  function renderProjects() {
    var list = $('project-list');
    clear(list);
    var filter = state.filter.toLowerCase();
    var shown = 0;

    for (var i = 0; i < state.projects.length; i++) {
      var project = state.projects[i];
      if (filter && (project.name + ' ' + project.id + ' ' + project.code).toLowerCase().indexOf(filter) < 0) continue;
      shown++;

      var row = el('div', 'proj-item');
      row.setAttribute('data-id', project.id);

      var main = el('div', 'proj-main');
      main.appendChild(el('div', 'proj-name', project.name));
      var meta = el('div', 'proj-meta');
      meta.textContent = project.game + ' · ' + project.files + ' файлов · ' + project.ops +
        ' операций · ' + project.modified;
      main.appendChild(meta);

      var side = el('div', 'proj-side');
      side.appendChild(el('span', 'pill', project.id));
      side.appendChild(el('span', 'pill code', project.code));
      if (project.members > 0) side.appendChild(el('span', 'pill ok', '● ' + project.members));
      row.appendChild(main);
      row.appendChild(side);

      row.addEventListener('click', function () {
        openProject(this.getAttribute('data-id'));
      });
      list.appendChild(row);
    }

    if (shown === 0) list.appendChild(el('div', 'empty', t('projects.empty')));
  }

  function renderStatus() {
    var status = state.status;
    if (!status) return;
    $('stat-projects').textContent = status.projects;
    $('stat-projects-sub').textContent = status.workspace;
    var online = 0;
    for (var i = 0; i < status.online.length; i++) online += status.online[i].members;
    $('stat-online').textContent = online;
    $('stat-jobs').textContent = status.jobs;
    $('stat-jobs-sub').textContent = status.requests + ' запросов, ' + status.errors + ' ошибок';
    $('stat-uptime').textContent = fmtUptime(status.uptimeSec);
    $('stat-port').textContent = status.bind + ':' + status.port;
    $('ver').textContent = 'v' + status.version;
    $('logs-file').textContent = status.logFile || '';
  }

  function renderJobs() {
    var box = $('job-list');
    clear(box);
    if (!state.jobs.length) {
      box.appendChild(el('div', 'empty', t('jobs.empty')));
      return;
    }
    for (var i = 0; i < state.jobs.length; i++) {
      var job = state.jobs[i];
      var card = el('div', 'job');
      var head = el('div', 'job-head');
      head.appendChild(el('span', 'mono small', job.id));
      head.appendChild(el('span', 'job-name', job.name));
      head.appendChild(el('span', 'job-state ' + job.state, t('job.' + job.state)));
      card.appendChild(head);

      var bar = el('div', 'bar');
      var fill = el('i');
      fill.style.width = Math.max(2, job.progress) + '%';
      bar.appendChild(fill);
      card.appendChild(bar);

      card.appendChild(el('div', 'job-msg', job.message || ''));
      box.appendChild(card);
    }
  }

  // ---------------------------------------------------------------- project

  function openProject(id) {
    global.Sound.play('click');
    API.project(id).then(function (data) {
      state.projectId = id;
      state.project = data.project;
      state.files = data.files;
      state.activity = 0;
      state.opsRenderCount = 0;
      clear($('proj-ops'));
      $('proj-title').textContent = data.project.name;
      $('proj-game').textContent = data.project.game + ' / ' + data.project.platform;
      $('proj-code').textContent = data.project.code;
      $('session-code').textContent = data.project.code;
      showView('project');
      renderFiles();
      renderOps(data.ops || []);
      renderSessionInfo();
      startSessionWait();
      selectProjectTab('tools');
      $('inspector').innerHTML = '<div class="empty">' + t('inspect.empty') + '</div>';
      $('inspector-path').textContent = '';
    }).catch(fail);
  }

  function renderFiles() {
    var list = $('file-list');
    clear(list);
    if (!state.files.length) {
      list.appendChild(el('div', 'empty', t('files.empty')));
      return;
    }
    for (var i = 0; i < state.files.length; i++) {
      (function (file) {
        var row = el('div', 'file');
        if (state.selectedFile === file.path) row.classList.add('active');
        var ext = file.path.indexOf('.') >= 0 ? file.path.split('.').pop() : '·';
        row.appendChild(el('span', 'file-ext', ext.substr(0, 4)));
        row.appendChild(el('span', 'file-name', file.path));
        row.appendChild(el('span', 'file-size', fmtSize(file.size)));
        row.addEventListener('click', function () { inspectFile(file.path); });
        list.appendChild(row);
      })(state.files[i]);
    }
  }

  function inspectFile(path) {
    state.selectedFile = path;
    renderFiles();
    $('inspector-path').textContent = path;
    global.Sound.play('click');
    API.inspect(state.projectId, path).then(function (data) {
      renderInspector(data);
    }).catch(function (error) {
      $('inspector').innerHTML = '<div class="empty">' + error.message + '</div>';
    });
  }

  function renderInspector(data) {
    var box = $('inspector');
    clear(box);

    var head = el('div', 'insp-head');
    head.appendChild(el('span', 'kind', data.kind));
    head.appendChild(el('span', 'kind-desc', data.label));
    if (data.platform && data.platform !== 'unknown') head.appendChild(el('span', 'pill', data.platform));
    if (data.game && data.game !== 'unknown') head.appendChild(el('span', 'pill', data.game));
    box.appendChild(head);

    if (data.details) {
      for (var key in data.details) {
        if (!Object.prototype.hasOwnProperty.call(data.details, key)) continue;
        var value = data.details[key];
        if (value === null || value === undefined || value === '') continue;
        if (typeof value === 'object') continue;
        var row = el('div', 'kv');
        row.appendChild(el('span', null, key));
        row.appendChild(el('b', null, String(value)));
        box.appendChild(row);
      }
    }

    if (data.error) box.appendChild(el('div', 'empty', data.error));

    if (data.textures && data.textures.length) {
      box.appendChild(el('h3', null, t('inspector.textures') + ' (' + data.textures.length + ')'));
      var grid = el('div', 'tex-grid');
      for (var i = 0; i < data.textures.length; i++) {
        (function (texture) {
          var card = el('div', 'tex-card');
          var img = document.createElement('img');
          img.decoding = 'sync';
          img.src = API.textureUrl(state.projectId, state.selectedFile, texture.index);
          img.alt = texture.name;
          card.appendChild(img);
          card.appendChild(el('div', 'tex-name', texture.name));
          card.appendChild(el('div', 'tex-meta', texture.width + '×' + texture.height + ' ' + texture.format));

          var replaceButton = el('button', 'tex-import', '↥');
          replaceButton.title = t('tex.import');
          replaceButton.addEventListener('click', function (event) {
            event.stopPropagation();
            pickTexture(replaceButton, texture);
          });
          card.appendChild(replaceButton);

          card.addEventListener('click', function () {
            window.open(API.textureUrl(state.projectId, state.selectedFile, texture.index), '_blank');
          });
          grid.appendChild(card);
        })(data.textures[i]);
      }
      box.appendChild(grid);
    }

    if (data.kind === 'gxt') {
      var gxtBar = el('div', 'insp-head');
      var download = el('button', 'btn tiny', t('gxt.download'));
      download.addEventListener('click', function () {
        window.open(API.gxtTextUrl(state.projectId, state.selectedFile), '_blank');
      });
      var upload = el('button', 'btn tiny', t('gxt.upload'));
      upload.addEventListener('click', function () { pickGxtText(); });
      gxtBar.appendChild(download);
      gxtBar.appendChild(upload);
      box.appendChild(gxtBar);
    }

    if (data.strings && data.strings.length) {
      box.appendChild(el('h3', null, t('inspector.strings') + ' (' + (data.stringCount || data.strings.length) + ')'));
      var table = el('table', 'grid');
      for (var s = 0; s < data.strings.length; s++) {
        var tr = document.createElement('tr');
        var keyCell = document.createElement('td');
        keyCell.className = 'mono';
        keyCell.textContent = data.strings[s].key;
        var textCell = document.createElement('td');
        textCell.textContent = data.strings[s].text;
        tr.appendChild(keyCell);
        tr.appendChild(textCell);
        table.appendChild(tr);
      }
      box.appendChild(table);
    }

    if (data.models && data.models.length) {
      box.appendChild(el('h3', null, t('inspector.models') + ' (' + data.models.length + ')'));
      var mtable = el('table', 'grid');
      var header = document.createElement('tr');
      ['id', 'name', 'hash', 'offset', 'sectors', 'texture'].forEach(function (name) {
        var th = document.createElement('th');
        th.textContent = name;
        header.appendChild(th);
      });
      mtable.appendChild(header);
      for (var m = 0; m < data.models.length; m++) {
        var model = data.models[m];
        var mtr = document.createElement('tr');
        [model.id, model.name, model.hash, model.offset, model.sectors, model.texName].forEach(function (v) {
          var td = document.createElement('td');
          td.textContent = v;
          mtr.appendChild(td);
        });
        mtable.appendChild(mtr);
      }
      box.appendChild(mtable);
    }

    if (data.kind === 'vag') {
      box.appendChild(el('h3', null, 'audio'));
      var audio = document.createElement('audio');
      audio.controls = true;
      audio.src = API.audioUrl(state.projectId, state.selectedFile);
      box.appendChild(audio);
    }
  }

  function pickGxtText() {
    var input = document.createElement('input');
    input.type = 'file';
    input.accept = '.txt,text/plain';
    input.style.display = 'none';
    document.body.appendChild(input);
    input.addEventListener('change', function () {
      var file = input.files && input.files[0];
      document.body.removeChild(input);
      if (!file) return;
      API.gxtImport(state.projectId, state.selectedFile, file)
        .then(function (data) {
          var changed = data.data ? data.data.changed : 0;
          toast(t('toast.gxtImported', changed), 'ok');
          global.Sound.play('ok');
          inspectFile(state.selectedFile);
          refreshProjectOps();
        })
        .catch(fail);
    });
    input.click();
  }

  function pickTexture(anchor, texture) {
    var input = document.createElement('input');
    input.type = 'file';
    input.accept = 'image/png';
    input.style.display = 'none';
    document.body.appendChild(input);
    input.addEventListener('change', function () {
      var file = input.files && input.files[0];
      document.body.removeChild(input);
      if (!file) return;
      API.textureImport(state.projectId, state.selectedFile, texture.index, file)
        .then(function (data) {
          toast(t('toast.texReplaced', texture.name), 'ok');
          global.Sound.play('ok');
          inspectFile(state.selectedFile);
          refreshProjectOps();
        })
        .catch(fail);
    });
    input.click();
  }

  // -------------------------------------------------------------- operations

  function renderOpsGrid() {
    var grid = $('op-grid');
    clear(grid);
    for (var i = 0; i < OP_DEFS.length; i++) {
      (function (def) {
        var card = el('div', 'op');
        card.appendChild(el('div', 'op-title', def.title));
        card.appendChild(el('div', 'op-desc', def.desc));

        var sourceInput = document.createElement('input');
        sourceInput.type = 'text';
        sourceInput.placeholder = def.source;
        card.appendChild(sourceInput);

        var targetInput = null;
        if (def.target) {
          targetInput = document.createElement('input');
          targetInput.type = 'text';
          targetInput.placeholder = def.target;
          card.appendChild(targetInput);
        }

        var extraInput = null;
        if (def.extra) {
          extraInput = document.createElement('input');
          extraInput.type = 'text';
          extraInput.placeholder = def.extra;
          card.appendChild(extraInput);
        }

        var row = el('div', 'row');
        var run = el('button', 'btn tiny primary', '▶ ' + def.name);
        run.addEventListener('click', function () {
          if (!sourceInput.value.trim()) {
            toast(t('toast.needPath'), 'warn');
            return;
          }
          runOperation(def, sourceInput.value.trim(),
            targetInput ? targetInput.value.trim() : '',
            extraInput ? extraInput.value.trim() : '');
        });
        row.appendChild(run);
        card.appendChild(row);
        grid.appendChild(card);
      })(OP_DEFS[i]);
    }
  }

  function runOperation(def, source, target, extra) {
    global.Sound.play('click');
    API.runOp({
      name: def.name,
      project: state.projectId,
      source: source,
      target: target,
      extra: extra,
      user: state.nick || 'web'
    }).then(function (data) {
      toast(t('toast.jobQueued', data.job.id), 'ok');
      watchJob(data.job.id);
      refreshJobs();
      refreshProject();
    }).catch(fail);
  }

  function watchJob(id) {
    if (state.activeJobs[id]) return;
    state.activeJobs[id] = true;
    var timer = setInterval(function () {
      API.job(id).then(function (data) {
        var job = data.job;
        if (job.state === 'done' || job.state === 'failed' || job.state === 'cancelled') {
          clearInterval(timer);
          delete state.activeJobs[id];
          if (job.state === 'done') {
            toast(t('toast.jobDone', job.name), 'ok');
            global.Sound.play('ok');
          } else if (job.state === 'failed') {
            toast(t('toast.jobFailed', job.name) + ' — ' + job.message, 'err');
            global.Sound.play('error');
          }
          refreshJobs();
          refreshProject();
        }
      }).catch(function () {
        clearInterval(timer);
        delete state.activeJobs[id];
      });
    }, 700);
  }

  // ----------------------------------------------------------------- session

  function renderSessionInfo() {
    $('session-mode').textContent = state.session && state.session.online
      ? t('session.online') : t('session.offline');
    var box = $('session-members');
    clear(box);
    if (!state.session || !state.session.members || !state.session.members.length) {
      box.appendChild(el('span', 'muted', '—'));
      $('proj-online').classList.add('hidden');
      return;
    }
    for (var i = 0; i < state.session.members.length; i++) {
      var member = state.session.members[i];
      var chip = el('span', 'member');
      chip.appendChild(el('span', 'avatar', initials(member.nick)));
      chip.appendChild(el('span', null, member.nick));
      box.appendChild(chip);
    }
    $('proj-online').classList.remove('hidden');
    $('proj-online').textContent = '● ' + state.session.members.length + ' online';
  }

  function refreshSession() {
    if (!state.projectId) return;
    API.session(state.projectId).then(function (data) {
      state.session = data;
      state.activity = data.activity;
      renderSessionInfo();
    }).catch(function () { });
  }

  function startSessionWait() {
    if (state.waiting) return;
    state.waiting = true;
    (function loop() {
      if (!state.projectId || state.view !== 'project') {
        state.waiting = false;
        return;
      }
      API.wait(state.projectId, state.activity, state.clientId, 15000).then(function (data) {
        state.activity = data.activity;
        state.session = {
          code: state.project ? state.project.code : '',
          online: true,
          members: data.members || []
        };
        renderSessionInfo();
        refreshProjectOps();
        loop();
      }).catch(function () {
        state.waiting = false;
        setTimeout(function () {
          if (state.projectId && state.view === 'project') startSessionWait();
        }, 4000);
      });
    })();
  }

  function joinSession() {
    var nick = $('session-nick').value.trim();
    if (!nick) { toast(t('toast.needNick'), 'warn'); $('session-nick').focus(); return; }
    state.nick = nick;
    try { localStorage.setItem('dpt.nick', nick); } catch (e) { }
    API.join(state.projectId, nick, state.clientId).then(function (data) {
      state.clientId = data.clientId;
      try { localStorage.setItem('dpt.client', data.clientId); } catch (e) { }
      $('session-mode').textContent = t('session.online');
      toast(t('toast.joined'), 'ok');
      global.Sound.play('join');
      refreshSession();
      refreshProjectOps();
    }).catch(fail);
  }

  function leaveSession() {
    if (!state.clientId) return;
    API.leave(state.projectId, state.clientId).then(function () {
      toast(t('toast.left'), 'ok');
      global.Sound.play('leave');
      state.clientId = null;
      try { localStorage.removeItem('dpt.client'); } catch (e) { }
      refreshSession();
    }).catch(fail);
  }

  function sendNote() {
    var text = $('note-text').value.trim();
    if (!text) return;
    API.note(state.projectId, text, state.nick || 'web').then(function () {
      $('note-text').value = '';
      toast(t('toast.noteAdded'), 'ok');
      global.Sound.play('message');
      refreshProjectOps();
    }).catch(fail);
  }

  // -------------------------------------------------------------------- ops

  function renderOps(ops) {
    var box = $('proj-ops');
    if (!ops || !ops.length) {
      if (!box.firstChild) box.appendChild(el('div', 'empty', '—'));
      return;
    }
    for (var i = 0; i < ops.length; i++) {
      var op = ops[i];
      var row = el('div', 'op-row');
      row.appendChild(el('span', 'when', (op.when || '').substr(11, 8)));
      row.appendChild(el('span', 'who', op.user || ''));
      row.appendChild(el('span', 'what', op.kind || ''));
      row.appendChild(el('span', 'note',
        (op.target && op.target !== '-' ? op.target + ' — ' : '') + (op.note || '')));
      box.appendChild(row);
      state.opsRenderCount++;
    }
    box.scrollTop = box.scrollHeight;
  }

  function refreshProjectOps() {
    if (!state.projectId) return;
    API.ops(state.projectId, 0).then(function (data) {
      state.opsRenderCount = 0;
      clear($('proj-ops'));
      renderOps(data.ops || []);
    }).catch(function () { });
  }

  function refreshProject() {
    if (!state.projectId) return;
    API.project(state.projectId).then(function (data) {
      state.project = data.project;
      state.files = data.files;
      renderFiles();
    }).catch(function () { });
  }

  // ------------------------------------------------------------------ files

  function uploadFiles(fileList) {
    if (!fileList || !fileList.length || !state.projectId) return;
    var files = Array.prototype.slice.call(fileList);
    var index = 0;

    function next() {
      if (index >= files.length) { refreshProject(); return; }
      var file = files[index++];
      API.upload(state.projectId, file.name, file).then(function () {
        toast(t('toast.uploaded', file.name), 'ok');
        next();
      }).catch(function (error) {
        fail(error);
        next();
      });
    }
    next();
  }

  // ------------------------------------------------------------------- logs

  function refreshLogs() {
    API.logs(300).then(function (data) {
      var box = $('logbox');
      if (!box) return;
      var text = '';
      for (var i = 0; i < data.lines.length; i++) text += data.lines[i] + '\n';
      box.textContent = text;
      if ($('logs-follow').checked) box.scrollTop = box.scrollHeight;
    }).catch(function () { });
  }

  // ------------------------------------------------------------------ about

  function renderAbout() {
    var body = $('about-body');
    if (body.childNodes.length) return;
    body.appendChild(el('p', null, t('about.intro')));
    body.appendChild(el('p', null,
      'Что работает сейчас: CSO/ZSO ↔ ISO, ISO 9660 (разбор и сборка), IMG (GTA III/VC/SA и Stories), ' +
      'GAME.DTZ ↔ GAME.DAT, таблица стриминга и экспорт .DIR, CHK/XTX текстуры (палитра, PSP swizzle, ' +
      'mip-цепочки, PNG), GXT текст (чтение и сборка), VAG ↔ WAV.'));

    body.appendChild(el('h3', null, 'CLI'));
    var pre = el('pre', 'logbox small');
    pre.textContent =
      'ModKit.exe dtz-dir GAME.DTZ lcs.dir\n' +
      'ModKit.exe img-list MODELS/GTA3PSPHR.IMG lcs.dir\n' +
      'ModKit.exe img-extract MODELS/GTA3PSPHR.IMG out --dir lcs.dir\n' +
      'ModKit.exe chk-export LOADSC0.CHK png\n' +
      'ModKit.exe iso-extract LCS.iso extracted\n' +
      'ModKit.exe iso-build extracted LCS_new.iso\n' +
      'ModKit.exe cso --compress LCS_new.iso LCS_new.cso';
    body.appendChild(pre);

    body.appendChild(el('h3', null, 'Проекты и сессии'));
    body.appendChild(el('p', null,
      'Проект — папка в workspace с манифестом project.json и рабочим деревом files/. ' +
      'Код сессии (ABCD-2345) связывает людей: изменения попадают в общий журнал операций, ' +
      'а клиенты получают их длинным опросом, поэтому всё работает за прокси и на старых машинах.'));

    var table = $('format-table');
    if (table.childNodes.length) return;
    var formats = [
      ['.CSO/.ZSO', 'сжатый образ (deflate) — распаковка и упаковка'],
      ['.ISO', 'ISO 9660 — список, распаковка, сборка'],
      ['.IMG', 'архив моделей — DIR / VER2 / скан чанков'],
      ['.DIR', 'таблица 32-байтных записей (сектора)'],
      ['.DTZ/.DAT', 'zlib + relocatable chunk GTAG'],
      ['.LVZ', 'zlib-сжатый WRLD чанк'],
      ['.CHK/.XTX', 'текстуры RslTexList (pal4/pal8/dxt)'],
      ['.MDL', 'модель (чанк ldm)'],
      ['.GXT', 'таблицы текста TABLD/TKEY'],
      ['.VAG/.AT3', 'аудио: PS-ADPCM ↔ WAV; AT3 только чтение'],
      ['.LUA.LC', 'байткод Lua 5.1 — только идентификация'],
      ['.PMF/.PSS', 'видео PSP (MPEG-PS) — только идентификация']
    ];
    for (var i = 0; i < formats.length; i++) {
      var row = el('div', 'fmt-row');
      row.appendChild(el('b', null, formats[i][0]));
      row.appendChild(el('span', null, formats[i][1]));
      table.appendChild(row);
    }
  }

  // ------------------------------------------------------------------ wiring

  function selectProjectTab(name) {
    var tabs = document.querySelectorAll('#proj-tabs .tab2');
    for (var i = 0; i < tabs.length; i++)
      tabs[i].classList.toggle('active', tabs[i].getAttribute('data-ptab') === name);
    var panes = ['tools', 'session', 'log'];
    for (var j = 0; j < panes.length; j++)
      $('ptab-' + panes[j]).classList.toggle('hidden', panes[j] !== name);
  }

  function applyTheme(theme) {
    document.body.setAttribute('data-theme', theme);
    try { localStorage.setItem('dpt.theme', theme); } catch (e) { }
  }

  function init() {
    global.I18N.init();
    global.I18N.fillSelect($('lang'));
    $('lang').addEventListener('change', function () {
      global.I18N.setLang(this.value);
      global.Sound.play('click');
    });

    var theme = 'light';
    try { theme = localStorage.getItem('dpt.theme') || 'light'; } catch (e) { }
    applyTheme(theme);

    $('theme').addEventListener('click', function () {
      var current = document.body.getAttribute('data-theme');
      var next = current === 'light' ? 'dark' : (current === 'dark' ? 'oled' : 'light');
      applyTheme(next);
      global.Sound.play('click');
    });

    $('sound').addEventListener('click', function () {
      var on = global.Sound.toggle();
      this.classList.toggle('on', on);
      if (on) global.Sound.play('click');
    });
    $('sound').classList.toggle('on', global.Sound.enabled);

    var tabs = document.querySelectorAll('#tabs .tab');
    for (var i = 0; i < tabs.length; i++) {
      tabs[i].addEventListener('click', function () {
        showView(this.getAttribute('data-view'));
        global.Sound.play('click');
      });
    }

    var ptabs = document.querySelectorAll('#proj-tabs .tab2');
    for (var j = 0; j < ptabs.length; j++) {
      ptabs[j].addEventListener('click', function () {
        selectProjectTab(this.getAttribute('data-ptab'));
        global.Sound.play('click');
      });
    }

    $('back-dash').addEventListener('click', function () {
      state.projectId = null;
      state.waiting = false;
      showView('dash');
      refreshProjects();
    });

    $('project-filter').addEventListener('input', function () {
      state.filter = this.value;
      renderProjects();
    });

    $('create-project').addEventListener('click', function () {
      var name = $('new-name').value.trim();
      if (!name) { $('new-name').focus(); return; }
      API.createProject({
        name: name,
        game: $('new-game').value,
        owner: $('new-owner').value.trim() || 'local',
        description: $('new-desc').value.trim()
      }).then(function (data) {
        toast(t('toast.created', data.id), 'ok');
        global.Sound.play('ok');
        $('new-name').value = '';
        $('new-desc').value = '';
        refreshProjects();
      }).catch(fail);
    });

    $('file-input').addEventListener('change', function () {
      uploadFiles(this.files);
      this.value = '';
    });

    $('session-join').addEventListener('click', joinSession);
    $('session-leave').addEventListener('click', leaveSession);
    $('note-send').addEventListener('click', sendNote);
    $('note-text').addEventListener('keydown', function (event) {
      if (event.keyCode === 13) sendNote();
    });

    $('identify-run').addEventListener('click', function () {
      var path = $('identify-path').value.trim();
      if (!path) return;
      API.identify(path).then(function (data) {
        $('identify-out').textContent = JSON.stringify(data.data, null, 2);
        toast(t('toast.identified'), 'ok');
        global.Sound.play('ok');
      }).catch(fail);
    });

    $('logs-follow').addEventListener('change', function () { state.logsFollow = this.checked; });

    try {
      state.nick = localStorage.getItem('dpt.nick') || '';
      state.clientId = localStorage.getItem('dpt.client') || null;
    } catch (e) { }
    $('session-nick').value = state.nick;

    renderOpsGrid();

    refreshStatus();
    refreshProjects();
    refreshJobs();

    setInterval(refreshStatus, 4000);
    setInterval(refreshJobs, 2500);
    setInterval(function () { if (state.view === 'dash') refreshProjects(); }, 6000);
    setInterval(function () { if (state.view === 'logs') refreshLogs(); }, 3000);
  }

  function refreshStatus() {
    API.status().then(function (data) {
      state.status = data;
      renderStatus();
    }).catch(function () { });
  }

  function refreshProjects() {
    API.projects().then(function (data) {
      state.projects = data.projects;
      renderProjects();
    }).catch(function () { });
  }

  function refreshJobs() {
    API.jobs().then(function (data) {
      state.jobs = data.jobs;
      renderJobs();
    }).catch(function () { });
  }

  global.App = {
    init: init,
    onLangChanged: function () {
      renderProjects();
      renderJobs();
      renderOpsGrid();
      renderAbout();
      if (state.view === 'logs') refreshLogs();
    },
    refresh: function () { refreshStatus(); refreshProjects(); refreshJobs(); }
  };

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})(window);