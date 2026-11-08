/* DPT.PSP GTA Modding Kit - tiny WebAudio "UI sounds" (no files, no CDN) */
(function (global) {
  'use strict';

  var ctx = null;
  var enabled = true;
  var volume = 0.18;

  try {
    var saved = localStorage.getItem('dpt.sound');
    if (saved === '0') enabled = false;
  } catch (e) { }

  function ensureContext() {
    if (ctx) return ctx;
    var Ctor = global.AudioContext || global.webkitAudioContext;
    if (!Ctor) return null;
    try { ctx = new Ctor(); } catch (e) { ctx = null; }
    return ctx;
  }

  // Soft two-note blip. Frequencies chosen so nothing sounds like a phone.
  function tone(freq, duration, type, gainPeak, delay) {
    var audio = ensureContext();
    if (!audio || !enabled) return;
    var start = audio.currentTime + (delay || 0);
    var osc = audio.createOscillator();
    var gain = audio.createGain();
    osc.type = type || 'sine';
    osc.frequency.setValueAtTime(freq, start);
    gain.gain.setValueAtTime(0.0001, start);
    gain.gain.exponentialRampToValueAtTime(Math.max(0.0001, gainPeak * volume), start + 0.012);
    gain.gain.exponentialRampToValueAtTime(0.0001, start + duration);
    osc.connect(gain);
    gain.connect(audio.destination);
    osc.start(start);
    osc.stop(start + duration + 0.02);
  }

  var SOUNDS = {
    click: function () { tone(660, 0.06, 'sine', 0.5); },
    ok: function () { tone(560, 0.09, 'sine', 0.6); tone(840, 0.12, 'sine', 0.4, 0.07); },
    error: function () { tone(240, 0.16, 'triangle', 0.5); tone(180, 0.2, 'triangle', 0.4, 0.08); },
    join: function () { tone(520, 0.1, 'sine', 0.5); tone(700, 0.1, 'sine', 0.4, 0.08); tone(880, 0.14, 'sine', 0.35, 0.16); },
    leave: function () { tone(600, 0.1, 'sine', 0.4); tone(420, 0.14, 'sine', 0.3, 0.08); },
    job: function () { tone(720, 0.07, 'sine', 0.35); },
    message: function () { tone(780, 0.06, 'sine', 0.35); tone(980, 0.08, 'sine', 0.25, 0.06); }
  };

  global.Sound = {
    play: function (name) {
      if (!enabled) return;
      var fn = SOUNDS[name];
      if (fn) { try { fn(); } catch (e) { } }
    },
    toggle: function () {
      enabled = !enabled;
      try { localStorage.setItem('dpt.sound', enabled ? '1' : '0'); } catch (e) { }
      if (enabled) this.play('click');
      return enabled;
    },
    get enabled() { return enabled; }
  };
})(window);