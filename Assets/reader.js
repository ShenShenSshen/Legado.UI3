'use strict';
(() => {
  const viewport = document.getElementById('viewport');
  const book = document.getElementById('book');
  const chapters = [];
  let mode = 'scroll', page = 0, pageCount = 1, pageStride = 0, scheduled = false;
  let token = '', restoreFrame = 0, lastPosition = null, restored = null;
  let layoutWidth = 0, layoutHeight = 0;
  let prefetchScreens = 2.25;
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
        image.addEventListener('load', () => { if (mode === 'paged' && section.isConnected) layoutChanged(true); });
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
  // Multi-column layout fragments paragraphs, so use the rectangle of a single character.
  function characterRect(text, offset) {
    const range = document.createRange();
    range.setStart(text, offset);
    range.setEnd(text, offset + 1);
    return range.getBoundingClientRect();
  }
  function firstAfter(count, before) {
    let start = 0, end = count;
    while (start < end) {
      const mid = Math.floor((start + end) / 2);
      if (before(mid)) start = mid + 1;
      else end = mid;
    }
    return start;
  }
  function captureAnchor(section) {
    const view = viewport.getBoundingClientRect();
    const before = rect => mode === 'paged' ? rect.right <= view.left : rect.bottom <= view.top;
    const block = firstAfter(section.children.length, i => {
      const rects = section.children[i].getClientRects();
      return !rects.length || before(rects[rects.length - 1]);
    });
    const element = section.children[block];
    if (!element) return null;
    const text = element.firstChild;
    const length = text instanceof Text ? text.length : 0;
    const offset = length ? firstAfter(length, i => before(characterRect(text, i))) : 0;
    if (length && offset === length) return null;
    const rect = length ? characterRect(text, offset) : element.getClientRects()[0];
    return rect ? { block, offset, length, inset:rect.top - view.top } : null;
  }
  function anchorRect(section, anchor) {
    if (!anchor || !Number.isInteger(anchor.block) || !Number.isInteger(anchor.offset) || !Number.isFinite(anchor.inset)) return null;
    const element = section.children[anchor.block];
    if (!element) return null;
    const text = element.firstChild;
    const length = text instanceof Text ? text.length : 0;
    if (length !== anchor.length || anchor.offset < 0 || anchor.offset >= Math.max(1, length)) return null;
    return length ? characterRect(text, anchor.offset) : element.getClientRects()[0];
  }
  function position() {
    const active = activeChapter();
    if (!active || !token || !viewport.clientHeight) return null;
    const ci = Number(active.dataset.ci);
    if (restored && (restored.ci !== ci || restored.page !== page || restored.top !== viewport.scrollTop)) restored = null;
    const anchor = restored?.anchor ?? captureAnchor(active);
    if (mode === 'paged') return { ci, anchor, pct:pageCount > 1 ? page / (pageCount - 1) : 0, page:page + 1, pages:pageCount };
    return { ci, anchor, pct:clamp((viewport.scrollTop - scrollOrigin(active)) / scrollRange(active)) };
  }
  function progress(requestNext = true) {
    syncLayout();
    const current = position();
    if (!current) return null;
    lastPosition = current;
    post({ type:'progress', ...current });
    if (mode === 'scroll' && requestNext && viewport.scrollTop + viewport.clientHeight * prefetchScreens >= viewport.scrollHeight)
      post({ type:'needNext', ci:chapters.at(-1).ci });
    return current;
  }
  function drawPage() {
    book.style.transform = mode === 'paged' ? 'translateX(' + (-page * pageStride) + 'px)' : '';
  }
  function repaginate(percent, section, anchor) {
    const style = getComputedStyle(viewport);
    const inset = parseFloat(style.paddingLeft) + parseFloat(style.paddingRight);
    const width = Math.max(1, viewport.clientWidth - inset);
    const gap = Math.max(56, inset + 2);
    pageStride = width + gap;
    book.style.transform = '';
    book.style.setProperty('--column', width + 'px');
    book.style.setProperty('--column-gap', gap + 'px');
    pageCount = Math.max(1, Math.round((book.scrollWidth + gap) / pageStride));
    const rect = anchorRect(section, anchor);
    page = rect ? Math.max(0, Math.min(pageCount - 1, Math.floor((rect.left - book.getBoundingClientRect().left) / pageStride)))
      : Math.round(clamp(percent) * (pageCount - 1));
    drawPage();
    return rect;
  }
  function readingWidth() { return mode === 'paged' ? viewport.clientWidth : book.clientWidth; }
  function seek(percent, section, anchor) {
    if (!section || !viewport.clientHeight) return;
    let rect;
    if (mode === 'paged') rect = repaginate(percent, section, anchor);
    else {
      book.style.transform = '';
      rect = anchorRect(section, anchor);
      viewport.scrollTop = rect ? viewport.scrollTop + rect.top - viewport.getBoundingClientRect().top - anchor.inset
        : scrollOrigin(section) + scrollRange(section) * clamp(percent);
    }
    layoutWidth = readingWidth();
    layoutHeight = viewport.clientHeight;
    restored = rect ? { ci:Number(section.dataset.ci), page, top:viewport.scrollTop, anchor } : null;
  }
  function syncLayout(force = false) {
    if (!lastPosition || !viewport.clientHeight) return;
    if (force || layoutWidth !== readingWidth() || (mode === 'paged' && layoutHeight !== viewport.clientHeight)) {
      const { ci, pct, anchor } = lastPosition;
      const section = book.querySelector('.chapter[data-ci="' + ci + '"]');
      seek(pct, section || activeChapter(), section ? anchor : null);
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
    prefetchScreens = settings.webChapterPrefetch ? 3.5 : 2.25;
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
    set(chapter, settings, newToken, percent = 0, anchor = null) {
      cancelAnimationFrame(restoreFrame);
      token = newToken;
      chapters.length = 0;
      chapters.push(chapter);
      const section = chapterNode(chapter);
      book.replaceChildren(section);
      viewport.scrollTop = 0;
      page = 0;
      lastPosition = null;
      restored = null;
      applySettings(settings);
      seek(percent, section, anchor);
      restoreFrame = requestAnimationFrame(() => {
        if (token !== newToken) return;
        if (!lastPosition) seek(percent, section, anchor);
        viewport.focus({ preventScroll:true });
        progress();
      });
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
      syncLayout();
      const section = activeChapter(), current = position();
      applySettings(settings);
      seek(current?.pct ?? 0, section, current?.anchor);
      progress();
    },
    report() { progress(); },
    position(requestNext = false) { return progress(requestNext); },
    chapterText(ci) { return chapters.find(chapter => chapter.ci === ci)?.paras || []; },
    step(direction) {
      if (mode === 'scroll') { viewport.scrollBy({ top:direction * viewport.clientHeight * .86 }); return; }
      if (page + direction < 0 || page + direction >= pageCount) post({ type:'chapter', direction });
      else { page += direction; drawPage(); progress(false); }
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
          progress(false);
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
  function layoutChanged(force = false) {
    syncLayout(force);
    progress(false);
  }
  new ResizeObserver(() => layoutChanged()).observe(viewport);
  document.fonts.addEventListener('loadingdone', () => {
    if (document.body.classList.contains('jetbrains')) layoutChanged(true);
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
