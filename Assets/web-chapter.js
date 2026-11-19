(selector => {
  if (!document.body || document.readyState === 'loading') return null;
  function visible(node) {
    if (!node.checkVisibility({ opacityProperty: true, visibilityProperty: true, contentVisibilityAuto: true })) return false;
    return !node.closest('[hidden],[inert],[aria-hidden="true"]');
  }
  const gate = /(?:此章为\s*VIP\s*章节|需要订阅后才能|订阅后(?:才可|可)?(?:继续)?阅读|请(?:先)?登录后(?:继续)?阅读|请完成(?:安全)?验证|仅供试读)/i;
  if (gate.test(document.body.innerText) ||
      [...document.querySelectorAll('dialog[open],[aria-modal="true"],[role="dialog"]')].some(visible))
    return { blocked: true };

  function paragraphs(node) {
    const lines = [];
    let line = '';
    const endLine = () => { if (line.trim()) lines.push(line.trim()); line = ''; };
    function visit(element) {
      if (!visible(element) || element.matches('script,style,button,nav,textarea,select,iframe')) return;
      if (element.matches('br')) { endLine(); return; }
      const block = !getComputedStyle(element).display.startsWith('inline');
      if (block) endLine();
      for (const child of element.childNodes) {
        if (child.nodeType === Node.ELEMENT_NODE) visit(child);
        if (child.nodeType !== Node.TEXT_NODE || !child.textContent.trim() && !line) continue;
        const range = new Range();
        range.selectNodeContents(child);
        const boxes = [...range.getClientRects()];
        if (!boxes.some(box => box.width > 0 && box.height > 0)) continue;
        let clipped = false;
        for (let parent = element; parent && !clipped; parent = parent.parentElement) {
          const overflow = getComputedStyle(parent).overflowY;
          if (overflow !== 'hidden' && overflow !== 'clip') continue;
          const limit = parent.getBoundingClientRect();
          clipped = boxes.some(box => box.top < limit.top - 1 || box.bottom > limit.bottom + 1);
        }
        if (!clipped) line += child.textContent.replace(/\s+/g, ' ');
      }
      if (block) endLine();
    }
    visit(node);
    endLine();
    return lines;
  }

  let content;
  try { if (selector) content = document.querySelector(selector); } catch { /* A book source may use a non-CSS rule. */ }
  const candidates = [content, ...document.querySelectorAll('#showReading,[itemprop="articleBody"],#chaptercontent,#chapter-content,main article,article')];
  for (const node of new Set(candidates)) {
    if (!node || node === document.body || node === document.documentElement || !visible(node)) continue;
    if ([...node.querySelectorAll('[role="status"]')].some(n => visible(n) && /加载中/.test(n.innerText))) continue;
    const paras = paragraphs(node);
    const length = paras.reduce((sum, text) => sum + text.length, 0);
    if (length < 24 || length > 500000 || paras.every(text => /^(?:正在)?(?:加载|读取)/.test(text))) continue;
    const heading = [...document.querySelectorAll('h1')].find(visible);
    const links = [...document.querySelectorAll('a[href]')];
    const directory = path => path.slice(0, path.lastIndexOf('/') + 1);
    const adjacent = (rel, label) => {
      const matches = links.filter(a => a.origin === location.origin && directory(a.pathname) === directory(location.pathname) &&
        (a.pathname !== location.pathname || a.search !== location.search) &&
        (a.relList.contains(rel) || label.test((a.getAttribute('title') || a.textContent).trim())));
      // Some sites hide their chapter controls in continuous-scroll mode.
      const link = matches.find(visible) || matches[0];
      if (!link) return null;
      return { url: link.href, title: (link.getAttribute('title') || link.textContent).trim().slice(0, 160) };
    };
    return { paras, title: (heading?.innerText || '').trim().slice(0, 160),
      previous: adjacent('prev', /^(?:上一章|前一章|上一回)$/),
      next: adjacent('next', /^(?:下一章|后一章|下一回)$/) };
  }
  return null;
})
