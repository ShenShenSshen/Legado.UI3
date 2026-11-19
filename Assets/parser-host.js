'use strict';
window.runEngine = ({ id, op, payload }) => {
  try {
    const parser = ParserModule.exports;
    const value = op === 'parseEpub' ? parser.parseEpub(payload.entries) :
      { title: payload.title, author: '', chapters: parser.splitTxt(payload.text) };
    chrome.webview.postMessage({ id, value });
  } catch (e) {
    chrome.webview.postMessage({ id, error: e.message || String(e) });
  }
};
chrome.webview.postMessage({ ready: true });
