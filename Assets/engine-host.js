'use strict';
(() => {
  function syncNet(url, options, jobId) {
    const xhr = new XMLHttpRequest();
    xhr.open('POST', '/__net?' + jobId, false);
    xhr.setRequestHeader('Content-Type', 'application/json');
    xhr.send(JSON.stringify({ ...options, url }));
    return JSON.parse(xhr.responseText);
  }
  window.__moreadHttpSync = syncNet;
  function exploreCategories(raw, source) {
    raw = String(raw ?? '').trim();
    let entries = raw;
    if (/^(?:@js:|<js>)/i.test(raw)) {
      const script = raw.replace(/^(?:@js:|<js>)/i, '').replace(/<\/js>\s*$/i, '');
      entries = Engine.exploreScript(script,
        { src: source, baseUrl: source.url });
      if (entries == null) throw new Error('此书源的发现脚本无法运行');
    }
    if (typeof entries === 'string' && entries.trim().startsWith('[')) entries = JSON.parse(entries);
    if (typeof entries === 'string') entries = entries.split(/\r?\n|&&(?=\s*[^&:\r\n]+::)/).map(line => {
      const parts = line.split('::');
      return parts.length > 1 ? { title: parts[0], url: parts[1] } : { title: '全部书籍', url: line };
    });
    if (!Array.isArray(entries)) throw new Error('无法识别此书源的浏览分类');
    const seen = new Set();
    return entries.flatMap(entry => {
      if (!entry || typeof entry !== 'object') return [];
      const title = String(entry.title || entry.name || '').trim();
      const url = String(entry.url || '').trim();
      if (!title || !url || seen.has(url + '\n' + title)) return [];
      seen.add(url + '\n' + title);
      return [{ title, url, paged: /\{\{?\s*page\s*\}\}?|<[^<>\r\n]*,[^<>\r\n]*>|^@js:|^<js>/i.test(url) }];
    });
  }
  chrome.webview.addEventListener('message', async ({ data: job }) => {
    const p = job.payload || {};
    const network = async options => {
      const response = await fetch('/__net?' + job.id, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(options) });
      return response.json();
    };
    try {
      let value;
      const source = p.source ? { ...Engine.normalizeSource(p.source), headers: p.source.headers, __moreadJobId: job.id } : null;
      switch (job.op) {
        case 'search': value = await Engine.search(o => network({ timeout: p.timeout || 10000, ...o }), source, p.key); break;
        case 'exploreCategories': {
          const raw = p.source.exploreUrl;
          value = exploreCategories(Array.isArray(raw) ? JSON.stringify(raw) : raw, source);
          break;
        }
        case 'exploreBooks': {
          const rules = { ...p.source.ruleSearch, ...Object.fromEntries(Object.entries(p.source.ruleExplore || {}).filter(([, value]) => value)) };
          if (!rules?.bookList) throw new Error('此书源没有配置发现书单规则');
          source.ruleSearch = rules;
          value = await Engine.search(network, source, '', { url: p.url, page: p.page });
          break;
        }
        case 'toc': value = await Engine.getToc(network, source, p.url, { expect: p.expect || 0 }); break;
        case 'content': value = await Engine.getContent(network, source, p.url); break;
        case 'detect': value = await Detect.one(source, { fetch: network, keyword: p.key || '我的', deep: p.deep !== false, timeout: 10000 }); break;
        case 'webSearch': {
          const result = await network({ url: p.url, timeout: 20000 });
          if (!result.ok || result.status >= 400) throw new Error(result.error || 'HTTP ' + result.status);
          if (Engine.pageBlocked(result.text)) throw new Error('搜索站点需要验证，请切换搜索引擎');
          const detected = Engine.detectPage(result.text, result.url || p.url);
          value = Engine.serpBooks(detected.links, p.key);
          break;
        }
        case 'inspectWeb':
          if (Engine.pageBlocked(p.html)) throw new Error('当前页面需要站点验证');
          value = Engine.detectPage(p.html, p.url);
          break;
        case 'matchChapter': {
          const clean = s => String(s || '').replace(/[\s\p{P}]/gu, '').toLowerCase();
          const wanted = clean(p.title), titles = p.chapters.map(clean);
          const number = Engine.chapNo(p.title);
          const guess = p.previousTotal > 0 ? p.index * titles.length / p.previousTotal : p.index;
          const match = predicate => titles.flatMap((t, i) => predicate(t, i) ? [i] : []);
          let matches = wanted ? match(t => t === wanted) : [];
          if (wanted && !matches.length) matches = match(t => t && (t.startsWith(wanted) || wanted.startsWith(t)));
          if (!matches.length && Number.isFinite(number)) matches = match((_, i) => Engine.chapNo(p.chapters[i]) === number);
          value = matches.length ? matches.reduce((best, i) => Math.abs(i - guess) < Math.abs(best - guess) ? i : best) : Math.round(guess);
          break;
        }
        case 'sniff': value = await Engine.sniffBook(network, p.url, { timeout: 20000 }); break;
        default: throw new Error('Unknown engine operation');
      }
      chrome.webview.postMessage({ id: job.id, value: value ?? null });
    } catch (e) { chrome.webview.postMessage({ id: job.id, error: e.message || String(e) }); }
  });
  chrome.webview.postMessage({ ready: true });
})();
