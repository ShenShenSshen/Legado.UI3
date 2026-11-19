'use strict';
window.ParserModule = { exports: {} };
window.parserRequire = name => {
  const normalize = value => {
    const parts = [];
    for (const p of String(value).split('/')) {
      if (!p || p === '.') continue;
      if (p === '..') parts.pop(); else parts.push(p);
    }
    return parts.join('/');
  };
  const path = {
    normalize,
    join: (...parts) => normalize(parts.join('/')),
    dirname: value => String(value).split('/').slice(0, -1).join('/') || '.',
    extname: value => /\.[^/.]+$/.exec(String(value))?.[0] || '',
    basename: (value, ext = '') => String(value).split('/').pop().replace(new RegExp(ext.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '$'), '')
  };
  path.posix = path;
  if (name === 'path') return path;
  if (name === './zip') return { readZip: entries => ({
    has: name => Object.hasOwn(entries, name),
    read(name) {
      const b64 = entries[name];
      return b64 === undefined ? null : Uint8Array.from(atob(b64), c => c.charCodeAt(0));
    },
    readText(name) { const bytes = this.read(name); return bytes ? new TextDecoder().decode(bytes) : null; }
  }) };
  throw new Error('Parser module is unavailable: ' + name);
};
