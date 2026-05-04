namespace Nocturne.API.Configuration;

/// <summary>
/// Lazy-loading mermaid renderer injected into the Scalar docs page.
/// Scans the rendered markdown for <c>pre &gt; code.language-mermaid</c>
/// blocks, defers loading the mermaid bundle until one is visible, then
/// upgrades the block in place. Idempotent and re-runs as Scalar streams
/// new content into the DOM (tag expansion, route navigation).
/// </summary>
internal static class MermaidLazyLoader
{
    public const string HeadContent = """
        <style>
          .nocturne-mermaid {
            display: flex;
            justify-content: center;
            margin: 1rem 0;
            min-height: 2rem;
          }
          .nocturne-mermaid[data-state="pending"]::before {
            content: "Loading diagram…";
            color: var(--scalar-color-3, #888);
            font-size: 0.85em;
          }
          .nocturne-mermaid svg { max-width: 100%; height: auto; }
        </style>
        <script type="module">
          let mermaidPromise = null;
          const loadMermaid = () => {
            mermaidPromise ??= import('https://cdn.jsdelivr.net/npm/mermaid@11/dist/mermaid.esm.min.mjs')
              .then((m) => {
                m.default.initialize({ startOnLoad: false, theme: 'dark', securityLevel: 'loose' });
                return m.default;
              });
            return mermaidPromise;
          };

          const io = new IntersectionObserver((entries) => {
            for (const entry of entries) {
              if (!entry.isIntersecting) continue;
              const el = entry.target;
              io.unobserve(el);
              loadMermaid().then(async (mermaid) => {
                try {
                  const id = 'm' + Math.random().toString(36).slice(2);
                  const { svg, bindFunctions } = await mermaid.render(id, el.dataset.source);
                  el.innerHTML = svg;
                  bindFunctions?.(el);
                  el.dataset.state = 'rendered';
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
        </script>
        """;
}
