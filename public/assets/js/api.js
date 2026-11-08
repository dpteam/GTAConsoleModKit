/* DPT.PSP GTA Modding Kit - HTTP client (ES5, XMLHttpRequest + JSON) */
(function (global) {
  'use strict';

  function request(method, url, body, contentType) {
    return new Promise(function (resolve, reject) {
      var xhr = new XMLHttpRequest();
      xhr.open(method, url, true);
      xhr.timeout = 120000;
      var token = readToken();
      if (token) xhr.setRequestHeader('X-DPT-Token', token);
      if (body !== undefined && body !== null) {
        xhr.setRequestHeader('Content-Type', contentType || 'application/json');
      }
      xhr.onreadystatechange = function () {
        if (xhr.readyState !== 4) return;
        var payload = null;
        if (xhr.responseText) {
          try { payload = JSON.parse(xhr.responseText); } catch (e) { payload = null; }
        }
        if (xhr.status >= 200 && xhr.status < 300) {
          resolve(payload);
        } else {
          var message = (payload && payload.error) || ('HTTP ' + xhr.status);
          var error = new Error(message);
          if (payload && payload.auth) error.auth = payload.auth;
          reject(error);
        }
      };
      xhr.ontimeout = function () { reject(new Error('timeout')); };
      xhr.onerror = function () { reject(new Error('network error')); };
      xhr.send(body === undefined ? null : body);
    });
  }

  function readToken() {
    try { return localStorage.getItem('dpt.token') || ''; } catch (e) { return ''; }
  }

  /// For URLs that end up in <img src>, <audio src> and <a href>: a header cannot
  /// travel there, so the token goes into the query string.
  function withToken(url) {
    var token = readToken();
    if (!token) return url;
    return url + (url.indexOf('?') >= 0 ? '&' : '?') + 'token=' + encode(token);
  }

  function encode(value) {
    return encodeURIComponent(value === undefined || value === null ? '' : value);
  }

  global.API = {
    get: function (url) { return request('GET', url); },
    post: function (url, data) { return request('POST', url, JSON.stringify(data || {})); },

    status: function () { return request('GET', '/api/status'); },
    health: function () { return request('GET', '/api/health'); },
    logs: function (lines) { return request('GET', '/api/logs?lines=' + (lines || 200)); },

    projects: function () { return request('GET', '/api/projects'); },
    project: function (id) { return request('GET', '/api/projects/' + encode(id)); },
    createProject: function (data) { return request('POST', '/api/projects', JSON.stringify(data)); },
    deleteProject: function (id) { return request('DELETE', '/api/projects/' + encode(id)); },

    // working tree: add a folder recursively, create a folder, delete a file or folder
    addFolder: function (id, path, as) {
      return request('POST', '/api/projects/' + encode(id) + '/folder',
        JSON.stringify({ path: path, as: as || '' }));
    },
    browse: function (path) {
      return request('GET', '/api/fs?path=' + encode(path || ''));
    },
    mkdir: function (id, path) {
      return request('POST', '/api/projects/' + encode(id) + '/mkdir', JSON.stringify({ path: path }));
    },
    imgTextures: function (id, path) {
      return request('GET', '/api/projects/' + encode(id) + '/img-textures?path=' + encode(path));
    },
    imgTextureUrl: function (id, path, entry, index, width) {
      return withToken('/api/projects/' + encode(id) + '/img-texture?path=' + encode(path) +
        '&entry=' + encode(entry) + '&index=' + index + (width ? '&w=' + width : ''));
    },
    buildProject: function (id, data) {
      return request('POST', '/api/projects/' + encode(id) + '/build', JSON.stringify(data || {}));
    },
    deletePath: function (id, path) {
      return request('POST', '/api/projects/' + encode(id) + '/delete', JSON.stringify({ path: path }));
    },

    ops: function (id, since) {
      return request('GET', '/api/projects/' + encode(id) + '/ops?since=' + (since || 0));
    },
    note: function (id, text, user) {
      return request('POST', '/api/projects/' + encode(id) + '/note',
        JSON.stringify({ text: text, user: user }));
    },

    inspect: function (id, path) {
      return request('GET', '/api/projects/' + encode(id) + '/inspect?path=' + encode(path));
    },
    textureUrl: function (id, path, index) {
      return withToken('/api/projects/' + encode(id) + '/texture?path=' + encode(path) + '&index=' + index);
    },
    audioUrl: function (id, path) {
      return withToken('/api/projects/' + encode(id) + '/audio?path=' + encode(path));
    },
    fileUrl: function (id, path) {
      return withToken('/api/projects/' + encode(id) + '/file?path=' + encode(path));
    },

    gxtTextUrl: function (id, path) {
      return withToken('/api/projects/' + encode(id) + '/gxt-text?path=' + encode(path));
    },
    gxtImport: function (id, path, file) {
      return new Promise(function (resolve, reject) {
        var xhr = new XMLHttpRequest();
        xhr.open('POST', '/api/projects/' + encode(id) + '/gxt-import?path=' + encode(path), true);
        var token = readToken();
        if (token) xhr.setRequestHeader('X-DPT-Token', token);
        xhr.setRequestHeader('Content-Type', 'text/plain; charset=utf-8');
        xhr.onreadystatechange = function () {
          if (xhr.readyState !== 4) return;
          var payload = null;
          if (xhr.responseText) { try { payload = JSON.parse(xhr.responseText); } catch (e) { } }
          if (xhr.status >= 200 && xhr.status < 300) resolve(payload);
          else {
            var error = new Error((payload && payload.error) || ('HTTP ' + xhr.status));
            if (payload && payload.auth) error.auth = payload.auth;
            reject(error);
          }
        };
        xhr.onerror = function () { reject(new Error('network error')); };
        xhr.send(file);
      });
    },

    textureImport: function (id, path, index, file) {
      return new Promise(function (resolve, reject) {
        var xhr = new XMLHttpRequest();
        xhr.open('POST', '/api/projects/' + encode(id) + '/texture-import?path=' +
          encode(path) + '&index=' + index, true);
        var token = readToken();
        if (token) xhr.setRequestHeader('X-DPT-Token', token);
        xhr.setRequestHeader('Content-Type', 'image/png');
        xhr.onreadystatechange = function () {
          if (xhr.readyState !== 4) return;
          var payload = null;
          if (xhr.responseText) { try { payload = JSON.parse(xhr.responseText); } catch (e) { } }
          if (xhr.status >= 200 && xhr.status < 300) resolve(payload);
          else reject(new Error((payload && payload.error) || ('HTTP ' + xhr.status)));
        };
        xhr.onerror = function () { reject(new Error('network error')); };
        xhr.send(file);
      });
    },

    upload: function (id, path, file) {
      return new Promise(function (resolve, reject) {
        var xhr = new XMLHttpRequest();
        xhr.open('POST', '/api/projects/' + encode(id) + '/upload?path=' + encode(path), true);
        var token = readToken();
        if (token) xhr.setRequestHeader('X-DPT-Token', token);
        xhr.setRequestHeader('Content-Type', 'application/octet-stream');
        xhr.onreadystatechange = function () {
          if (xhr.readyState !== 4) return;
          var payload = null;
          if (xhr.responseText) { try { payload = JSON.parse(xhr.responseText); } catch (e) { } }
          if (xhr.status >= 200 && xhr.status < 300) resolve(payload);
          else {
            var error = new Error((payload && payload.error) || ('HTTP ' + xhr.status));
            if (payload && payload.auth) error.auth = payload.auth;
            reject(error);
          }
        };
        xhr.onerror = function () { reject(new Error('network error')); };
        xhr.send(file);
      });
    },

    session: function (id) { return request('GET', '/api/projects/' + encode(id) + '/session'); },
    join: function (id, nick, clientId) {
      return request('POST', '/api/projects/' + encode(id) + '/session/join',
        JSON.stringify({ nick: nick, clientId: clientId }));
    },
    leave: function (id, clientId) {
      return request('POST', '/api/projects/' + encode(id) + '/session/leave',
        JSON.stringify({ clientId: clientId }));
    },
    wait: function (id, activity, clientId, ms) {
      return request('GET', '/api/projects/' + encode(id) + '/session/wait?activity=' +
        activity + '&clientId=' + encode(clientId) + '&ms=' + (ms || 15000));
    },

    job: function (id) { return request('GET', '/api/jobs/' + encode(id)); },
    jobs: function () { return request('GET', '/api/jobs?count=40'); },
    cancelJob: function (id) { return request('POST', '/api/jobs/' + encode(id) + '/cancel', '{}'); },

    runOp: function (params) {
      return request('POST', '/api/ops', JSON.stringify(params));
    },
    identify: function (path) {
      return request('GET', '/api/identify?path=' + encode(path));
    }
  };
})(window);