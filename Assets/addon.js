'use strict';
const panel = document.getElementById('panel');
chrome.webview.addEventListener('message', ({ data }) => {
  if (typeof data.html === 'string') panel.srcdoc = data.html;
  else if (data.moread === 'context') panel.contentWindow?.postMessage(data, '*');
});
window.addEventListener('message', event => {
  if (event.source === panel.contentWindow && ['context', 'toast', 'shelf', 'search', 'openBook'].includes(event.data?.moread))
    chrome.webview.postMessage(event.data);
});
chrome.webview.postMessage({ moread:'ready' });
