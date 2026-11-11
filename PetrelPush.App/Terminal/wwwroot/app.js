(function () {
  'use strict';

  var bridge = window.chrome && window.chrome.webview ? window.chrome.webview : null;
  var container = document.getElementById('terminal');

  var term = new Terminal({
    cursorBlink: true,
    cursorStyle: 'bar',
    fontFamily: 'Cascadia Mono, Consolas, "Microsoft YaHei Mono", "Courier New", monospace',
    fontSize: 14,
    lineHeight: 1.2,
    letterSpacing: 0,
    scrollback: 5000,
    allowProposedApi: true,
    macOptionIsMeta: false,
    rightClickSelectsWord: false,
    theme: {
      background: '#1B1B26',
      foreground: '#E6E1F5',
      cursor: '#9254DE',
      cursorAccent: '#1B1B26',
      selectionBackground: 'rgba(146, 84, 222, 0.35)',
      black: '#2A2438',
      red: '#F07178',
      green: '#7FD962',
      yellow: '#E6C07B',
      blue: '#82AAFF',
      magenta: '#C792EA',
      cyan: '#89DDFF',
      white: '#D7D2E5',
      brightBlack: '#6E6780',
      brightRed: '#FF8B92',
      brightGreen: '#A6E22E',
      brightYellow: '#FFCB6B',
      brightBlue: '#A4C4FF',
      brightMagenta: '#DDB6F2',
      brightCyan: '#B4E7FF',
      brightWhite: '#FFFFFF'
    }
  });

  var fit = new FitAddon.FitAddon();
  term.loadAddon(fit);
  term.open(container);

  function post(message) {
    if (bridge) {
      bridge.postMessage(message);
    }
  }

  // 会话失败后进入「等按键重试」状态：此时任意按键都用于触发重连
  var awaitingRetry = false;

  var NOTICE_COLORS = { info: '38;5;141', error: '38;5;203', hint: '38;5;245' };

  function notify(text, color) {
    var code = NOTICE_COLORS[color] || NOTICE_COLORS.info;
    var lines = String(text === undefined || text === null ? '' : text).split(/\r?\n/);
    for (var i = 0; i < lines.length; i++) {
      term.write('\r\n\x1b[' + code + 'm' + lines[i] + '\x1b[0m');
    }
    term.write('\r\n');
  }

  var lastCols = 0;
  var lastRows = 0;

  function applyFit() {
    try {
      fit.fit();
    } catch (e) {
      // 容器尚未布局完成时 fit 会失败，忽略即可，下次尺寸变化会重试
    }
    return term.cols > 0 && term.rows > 0;
  }

  function reportSize(force) {
    if (!applyFit()) {
      return;
    }
    if (!force && term.cols === lastCols && term.rows === lastRows) {
      return;
    }
    lastCols = term.cols;
    lastRows = term.rows;
    post({ type: 'resize', cols: term.cols, rows: term.rows });
  }

  term.onData(function (data) {
    if (awaitingRetry) {
      awaitingRetry = false;
      post({ type: 'retry' });
      return;
    }

    post({ type: 'input', data: data });
  });

  // 让 xterm 自己处理粘贴（WebView2 侧已关闭浏览器加速键，Ctrl+V 走 DOM paste 事件）
  term.attachCustomKeyEventHandler(function (event) {
    if (event.type !== 'keydown') {
      return true;
    }
    if (event.ctrlKey && event.shiftKey && event.code === 'KeyC') {
      var selection = term.getSelection();
      if (selection) {
        term.clearSelection();
        post({ type: 'copy', text: selection });
        return false;
      }
    }
    return true;
  });

  // 右键：有选中内容时复制并清除选区，否则直接把剪贴板内容粘贴到终端。
  // WebView2 自带的右键菜单已在宿主侧关闭，这里统一接管。
  container.addEventListener('contextmenu', function (event) {
    event.preventDefault();

    var selection = term.getSelection();
    if (selection) {
      term.clearSelection();
      post({ type: 'copy', text: selection });
      return;
    }

    post({ type: 'paste' });
  });

  if (bridge) {
    bridge.addEventListener('message', function (event) {
      var message = event.data;
      if (!message || !message.type) {
        return;
      }

      if (message.type === 'data') {
        // base64 -> 原始字节：交给 xterm 的 UTF-8 解码器，跨块的半个中文字符不会乱码
        var binary = atob(message.b64);
        var length = binary.length;
        var bytes = new Uint8Array(length);
        for (var i = 0; i < length; i++) {
          bytes[i] = binary.charCodeAt(i);
        }
        term.write(bytes);
        return;
      }

      if (message.type === 'notice') {
        notify(message.text, message.color);
        return;
      }

      if (message.type === 'state') {
        awaitingRetry = message.value === 'failed';
        return;
      }

      if (message.type === 'paste') {
        if (typeof message.text === 'string' && message.text.length > 0) {
          // 交给 xterm 处理换行规范化，再经 onData 发往 shell
          term.paste(message.text);
        }
        return;
      }

      if (message.type === 'reset') {
        term.reset();
        return;
      }

      if (message.type === 'focus') {
        term.focus();
      }
    });
  }

  var resizeTimer = 0;
  window.addEventListener('resize', function () {
    clearTimeout(resizeTimer);
    resizeTimer = setTimeout(function () { reportSize(false); }, 60);
  });

  if (typeof ResizeObserver !== 'undefined') {
    new ResizeObserver(function () {
      clearTimeout(resizeTimer);
      resizeTimer = setTimeout(function () { reportSize(false); }, 60);
    }).observe(container);
  }

  // 首帧完成后再量尺寸，确保容器已是最终大小
  requestAnimationFrame(function () {
    requestAnimationFrame(function () {
      applyFit();
      lastCols = term.cols;
      lastRows = term.rows;
      term.focus();
      post({ type: 'ready', cols: term.cols, rows: term.rows });
    });
  });
})();
