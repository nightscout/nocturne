let pelePromise = null;
const loadPele = () => {
  pelePromise ??= import('https://cdn.jsdelivr.net/npm/pele@1.6.0/dist/pele.js');
  return pelePromise;
};

// The full ER diagram is ~60k characters, past pele's 50k default.
const SOURCE_LIMIT = 250_000;

const addFullscreenButton = (frame) => {
  const fs = document.createElement('button');
  fs.type = 'button';
  fs.className = 'nocturne-mermaid-fullscreen';
  fs.textContent = 'Fullscreen';
  fs.addEventListener('click', async () => {
    try {
      if (document.fullscreenElement === frame) {
        await document.exitFullscreen();
      } else {
        await frame.requestFullscreen();
      }
    } catch (err) {
      console.warn('Fullscreen failed:', err);
    }
  });
  frame.addEventListener('fullscreenchange', () => {
    fs.textContent = document.fullscreenElement === frame ? 'Exit' : 'Fullscreen';
  });
  frame.appendChild(fs);
};

const io = new IntersectionObserver((entries) => {
  for (const entry of entries) {
    if (!entry.isIntersecting) continue;
    const el = entry.target;
    io.unobserve(el);
    loadPele().then(({ mount }) => {
      el.dataset.state = 'rendered';
      mount(el, el.dataset.source, {
        idPrefix: 'm' + Math.random().toString(36).slice(2) + '-',
        // Labels are measured in the page's font so the boxes fit Scalar's text.
        fontFamily: getComputedStyle(el).fontFamily,
        limit: SOURCE_LIMIT,
        onError: () => { el.dataset.state = 'error'; },
      });
    }, (err) => {
      el.dataset.state = 'error';
      el.textContent = 'Diagram failed to load: ' + err.message;
    });
  }
}, { rootMargin: '200px' });

const upgrade = (root) => {
  const blocks = root.querySelectorAll('pre > code.language-mermaid');
  for (const code of blocks) {
    const pre = code.parentElement;
    if (!pre || pre.dataset.mermaidUpgraded) continue;
    pre.dataset.mermaidUpgraded = '1';
    const frame = document.createElement('div');
    frame.className = 'nocturne-mermaid-frame';
    const container = document.createElement('div');
    container.className = 'nocturne-mermaid';
    container.dataset.state = 'pending';
    container.dataset.source = code.textContent ?? '';
    frame.appendChild(container);
    pre.replaceWith(frame);
    addFullscreenButton(frame);
    io.observe(container);
  }
};

const scan = () => upgrade(document);
const mo = new MutationObserver(() => scan());
mo.observe(document.body, { childList: true, subtree: true });
if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', scan, { once: true });
} else {
  scan();
}
