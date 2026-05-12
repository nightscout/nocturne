// svg-pan-zoom ships as a UMD bundle (no ESM build), so it has
// to be loaded via a <script> tag rather than dynamic import().
const loadSvgPanZoom = () => new Promise((resolve, reject) => {
  if (window.svgPanZoom) return resolve(window.svgPanZoom);
  const s = document.createElement('script');
  s.src = 'https://cdn.jsdelivr.net/npm/svg-pan-zoom@3.6.2/dist/svg-pan-zoom.min.js';
  s.onload = () => resolve(window.svgPanZoom);
  s.onerror = () => reject(new Error('Failed to load svg-pan-zoom'));
  document.head.appendChild(s);
});

let depsPromise = null;
const loadDeps = () => {
  depsPromise ??= Promise.all([
    import('https://cdn.jsdelivr.net/npm/mermaid@11/dist/mermaid.esm.min.mjs'),
    loadSvgPanZoom(),
  ]).then(([m, svgPanZoom]) => {
    m.default.initialize({ startOnLoad: false, theme: 'dark', securityLevel: 'loose' });
    return { mermaid: m.default, svgPanZoom };
  });
  return depsPromise;
};

const io = new IntersectionObserver((entries) => {
  for (const entry of entries) {
    if (!entry.isIntersecting) continue;
    const el = entry.target;
    io.unobserve(el);
    loadDeps().then(async ({ mermaid, svgPanZoom }) => {
      try {
        const id = 'm' + Math.random().toString(36).slice(2);
        const { svg, bindFunctions } = await mermaid.render(id, el.dataset.source);
        el.innerHTML = svg;
        bindFunctions?.(el);
        el.dataset.state = 'rendered';

        const svgEl = el.querySelector('svg');
        if (svgEl && svgPanZoom) {
          // Mermaid sets explicit width/height; clear so CSS sizing
          // (100% of the fixed-height container) takes over and
          // svg-pan-zoom can compute correctly.
          svgEl.removeAttribute('style');
          const panZoom = svgPanZoom(svgEl, {
            zoomEnabled: true,
            controlIconsEnabled: false,
            fit: true,
            center: true,
            minZoom: 0.5,
            maxZoom: 20,
            zoomScaleSensitivity: 0.3,
          });

          const refit = () => {
            panZoom.resize();
            panZoom.fit();
            panZoom.center();
          };

          const controls = document.createElement('div');
          controls.className = 'nocturne-mermaid-controls';

          const reset = document.createElement('button');
          reset.type = 'button';
          reset.textContent = 'Reset';
          reset.addEventListener('click', refit);

          const fs = document.createElement('button');
          fs.type = 'button';
          fs.textContent = 'Fullscreen';
          fs.addEventListener('click', async () => {
            try {
              if (document.fullscreenElement === el) {
                await document.exitFullscreen();
              } else {
                await el.requestFullscreen();
              }
            } catch (err) {
              console.warn('Fullscreen failed:', err);
            }
          });
          el.addEventListener('fullscreenchange', () => {
            fs.textContent = document.fullscreenElement === el ? 'Exit' : 'Fullscreen';
            // Wait one frame so the layout has flipped before refitting.
            requestAnimationFrame(refit);
          });

          controls.append(reset, fs);
          el.appendChild(controls);

          // Re-fit on container resize so the diagram stays framed.
          const ro = new ResizeObserver(refit);
          ro.observe(el);
        }
      } catch (err) {
        el.dataset.state = 'error';
        el.textContent = 'Diagram failed to render: ' + err.message;
      }
    });
  }
}, { rootMargin: '200px' });

const upgrade = (root) => {
  const blocks = root.querySelectorAll('pre > code.language-mermaid');
  for (const code of blocks) {
    const pre = code.parentElement;
    if (!pre || pre.dataset.mermaidUpgraded) continue;
    pre.dataset.mermaidUpgraded = '1';
    const container = document.createElement('div');
    container.className = 'nocturne-mermaid';
    container.dataset.state = 'pending';
    container.dataset.source = code.textContent ?? '';
    pre.replaceWith(container);
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
