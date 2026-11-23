'use strict';
(() => {
  const viewport = document.getElementById('viewport');
  const book = document.getElementById('book');
  const chapters = [];
  let mode = 'scroll', page = 0, pageCount = 1, pageStride = 0, scheduled = false;
  let token = '', lastPercent = 0, restoreFrame = 0;
  const post = value => chrome.webview.postMessage({ ...value, token });
  const isImage = value => /^(data:image\/(?:png|jpeg|jpg|gif|webp|avif|bmp|svg\+xml);|https?:\/\/)/i.test(value);
  const clamp = value => Math.max(0, Math.min(1, value || 0));

  function chapterNode(chapter) {
    const section = document.createElement('section');
    section.className = 'chapter';
    section.dataset.ci = chapter.ci;
    const heading = document.createElement('h1');
    heading.textContent = chapter.title;
    section.append(heading);
    for (const [index, content] of (chapter.paras || []).entries()) {
      if (typeof content !== 'string') continue;
      const imageUrl = content.startsWith('@img:') ? content.slice(5) : content.startsWith('data:image/') ? content : '';
      if (imageUrl && isImage(imageUrl)) {
        const figure = document.createElement('figure');
        const image = document.createElement('img');
        image.alt = '正文插图';
        image.addEventListener('load', () => { if (mode === 'paged' && section.isConnected) repaginate(lastPercent); });
        image.src = imageUrl;
        figure.append(image);
        section.append(figure);
      } else {
        const paragraph = document.createElement('p');
        paragraph.dataset.index = index;
        paragraph.textContent = content;
        section.append(paragraph);
      }
    }
    return section;
  }

  function activeChapter() {
    const sections = [...book.querySelectorAll('.chapter')];
    if (mode === 'paged') return sections[0];
    return sections.filter(section => section.offsetTop <= viewport.scrollTop + 66).pop() || sections[0];
  }
  function scrollOrigin(section) {
    return Math.max(0, section.offsetTop - parseFloat(getComputedStyle(viewport).paddingTop));
  }
  function scrollRange(section) {
    const bottomPadding = parseFloat(getComputedStyle(viewport).paddingBottom);
    return Math.max(1, section.offsetHeight + section.offsetTop - scrollOrigin(section) - viewport.clientHeight + bottomPadding);
  }
  function progress(requestNext = true) {
    const active = activeChapter();
    if (!active || !token || !viewport.clientHeight) return;
    const ci = Number(active.dataset.ci);
    if (mode === 'paged') {
      lastPercent = pageCount > 1 ? page / (pageCount - 1) : 0;
      post({ type:'progress', ci, pct:lastPercent, page:page + 1, pages:pageCount });
      return;
    }
    lastPercent = clamp((viewport.scrollTop - scrollOrigin(active)) / scrollRange(active));
    post({ type:'progress', ci, pct:lastPercent });
    if (requestNext && viewport.scrollTop + viewport.clientHeight * 2.25 >= viewport.scrollHeight)
      post({ type:'needNext', ci:chapters.at(-1).ci });
  }
  function drawPage() {
    book.style.transform = mode === 'paged' ? 'translateX(' + (-page * pageStride) + 'px)' : '';
    progress(false);
  }
  function repaginate(percent) {
    const style = getComputedStyle(viewport);
    const inset = parseFloat(style.paddingLeft) + parseFloat(style.paddingRight);
    const width = Math.max(1, viewport.clientWidth - inset);
    const gap = Math.max(56, inset + 2);
    pageStride = width + gap;
    book.style.transform = '';
    book.style.setProperty('--column', width + 'px');
    book.style.setProperty('--column-gap', gap + 'px');
    pageCount = Math.max(1, Math.round((book.scrollWidth + gap) / pageStride));
    page = Math.round(clamp(percent) * (pageCount - 1));
    drawPage();
  }
  function seek(percent, section = activeChapter()) {
    if (!section || !viewport.clientHeight) return;
    if (mode === 'paged') repaginate(percent);
    else {
      book.style.transform = '';
      viewport.scrollTop = scrollOrigin(section) + scrollRange(section) * clamp(percent);
      progress();
    }
  }
  function setTheme(theme) {
    const palettes = { paper:['#fafafa','#282828'], sepia:['#f5efdf','#373129'], green:['#e8efdf','#293426'], night:['#202020','#dedede'] };
    const [paper, ink] = palettes[theme] || palettes.paper;
    const root = document.documentElement;
    root.style.setProperty('--paper', paper);
    root.style.setProperty('--ink', ink);
    root.style.colorScheme = theme === 'night' ? 'dark' : 'light';
  }
  function applySettings(settings) {
    mode = settings.pageMode || 'scroll';
    document.body.classList.toggle('paged', mode === 'paged');
    document.body.classList.toggle('sans', settings.fontFamily === 'sans');
    document.body.classList.toggle('jetbrains', settings.fontFamily === 'jetbrains');
    setTheme(settings.theme);
    for (const [name, value] of Object.entries({
      width:(settings.readingWidth || 760)+'px', size:(settings.fontSize || 22)+'px',
      line:settings.lineHeight || 1.7, gap:(settings.paraGapEm ?? .5)+'em'
    })) document.documentElement.style.setProperty('--' + name, value);
  }

  window.reader = {
    theme: setTheme,
    set(chapter, settings, newToken, percent = 0) {
      cancelAnimationFrame(restoreFrame);
      token = newToken;
      chapters.length = 0;
      chapters.push(chapter);
      book.replaceChildren(chapterNode(chapter));
      viewport.scrollTop = 0;
      page = 0;
      lastPercent = percent;
      applySettings(settings);
      seek(percent);
      restoreFrame = requestAnimationFrame(() => { if (token === newToken) { seek(percent); viewport.focus({ preventScroll:true }); } });
    },
    append(chapter) {
      if (mode !== 'scroll' || chapters.some(c => c.ci === chapter.ci)) return;
      document.getElementById('retry-next')?.remove();
      chapters.push(chapter);
      book.append(chapterNode(chapter));
      const active = activeChapter();
      if (chapters.length > 4 && active && Number(active.dataset.ci) - chapters[0].ci > 2) {
        const topBefore = active.getBoundingClientRect().top;
        book.firstElementChild.remove();
        chapters.shift();
        // Compensate only the movement that the browser's scroll anchoring did not handle.
        const shift = active.getBoundingClientRect().top - topBefore;
        if (shift) viewport.scrollTop += shift;
      }
    },
    showNextRetry() {
      if (document.getElementById('retry-next')) return;
      const retry = document.createElement('button');
      retry.id = 'retry-next';
      retry.textContent = '重试加载下一章';
      retry.onclick = e => { e.stopPropagation(); post({ type:'retryNext', ci:chapters.at(-1).ci }); };
      book.append(retry);
    },
    settings(settings) {
      const section = activeChapter(), percent = lastPercent;
      applySettings(settings);
      seek(percent, section);
    },
    report() { progress(); },
    chapterText(ci) { return chapters.find(chapter => chapter.ci === ci)?.paras || []; },
    step(direction) {
      if (mode === 'scroll') { viewport.scrollBy({ top:direction * viewport.clientHeight * .86 }); return; }
      if (page + direction < 0 || page + direction >= pageCount) post({ type:'chapter', direction });
      else { page += direction; drawPage(); }
    },
    highlight(ci, index) {
      book.querySelector('.speaking')?.classList.remove('speaking');
      if (index < 0) return;
      const element = book.querySelector('.chapter[data-ci="' + ci + '"] p[data-index="' + index + '"]');
      if (!element) return;
      element.classList.add('speaking');
      if (mode === 'scroll') element.scrollIntoView({ block:'center' });
      else {
        const rect = element.getClientRects()[0], view = viewport.getBoundingClientRect();
        if (rect && (rect.right < view.left || rect.left > view.right)) {
          const delta = Math.floor((rect.left - view.left) / pageStride);
          page = Math.max(0, Math.min(pageCount - 1, page + delta));
          drawPage();
        }
      }
    },
    firstVisible(ci) {
      const view = viewport.getBoundingClientRect();
      const blocks = book.querySelectorAll('.chapter[data-ci="' + ci + '"] p[data-index]');
      const visible = [...blocks].find(p => [...p.getClientRects()].some(r => r.bottom > view.top && r.top < view.bottom && r.right > view.left && r.left < view.right));
      return visible ? Number(visible.dataset.index) : 0;
    }
  };
  viewport.addEventListener('scroll', () => {
    if (scheduled) return;
    scheduled = true;
    requestAnimationFrame(() => { scheduled = false; progress(); });
  });
  function layoutChanged() {
    if (!book.firstChild) return;
    if (mode === 'paged') seek(lastPercent);
    else progress(false);
  }
  new ResizeObserver(layoutChanged).observe(viewport);
  document.fonts.addEventListener('loadingdone', () => {
    if (document.body.classList.contains('jetbrains')) layoutChanged();
  });
  window.addEventListener('keydown', e => {
    const key = e.key.toLowerCase();
    if (['arrowright','pagedown',' ','arrowleft','pageup'].includes(key) && !(e.target instanceof HTMLButtonElement && key === ' ')) {
      e.preventDefault();
      const direction = ['arrowleft','pageup'].includes(key) || (key === ' ' && e.shiftKey) ? -1 : 1;
      if (mode === 'scroll' && key !== ' ') post({ type:'chapter', direction }); else reader.step(direction);
    } else if (key === 'escape') post({ type:'escape' });
    else if (key === 'c') post({ type:'toc' });
    else if (key === 'b') post({ type:'bookmark' });
    else if (key === 'f11' || key === 'f') { e.preventDefault(); post({ type:'fullscreen' }); }
    else if (key === '+' || key === '-' || key === '=') post({ type:'font', direction:key === '-' ? -1 : 1 });
  });
  let lastWheelAt = 0;
  viewport.addEventListener('wheel', e => {
    if (mode !== 'paged') return;
    e.preventDefault();
    const now = performance.now();
    if (Math.abs(e.deltaY) > 12 && now - lastWheelAt > 200) { lastWheelAt = now; reader.step(Math.sign(e.deltaY)); }
  }, { passive:false });
  let touchX = null;
  viewport.addEventListener('touchstart', e => { touchX = e.touches[0]?.clientX; }, { passive:true });
  viewport.addEventListener('touchend', e => {
    const delta = (e.changedTouches[0]?.clientX ?? 0) - touchX;
    if (mode === 'paged' && touchX !== null && Math.abs(delta) >= 48) reader.step(-Math.sign(delta));
    touchX = null;
  }, { passive:true });
  viewport.addEventListener('click', e => {
    if (window.getSelection().toString()) return;
    if (mode === 'paged') {
      if (e.clientX < innerWidth * .2) { reader.step(-1); return; }
      if (e.clientX > innerWidth * .8) { reader.step(1); return; }
    }
    post({ type:'toggleBars' });
  });
  chrome.webview.postMessage({ ready:true });
})();
