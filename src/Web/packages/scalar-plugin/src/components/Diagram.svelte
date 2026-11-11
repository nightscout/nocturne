<script lang="ts" module>
  // Loaded on the first diagram that scrolls near the viewport, and once for the page.
  const PELE = 'https://cdn.jsdelivr.net/npm/pele@1.6.0/dist/pele.js';

  type Pele = {
    mount: (
      target: HTMLElement,
      source: string,
      options: { idPrefix: string; fontFamily: string; limit: number; onError: () => void },
    ) => { destroy?: () => void } | void;
  };

  let pele: Promise<Pele> | undefined;
  const loadPele = () => (pele ??= import(/* @vite-ignore */ PELE) as Promise<Pele>);

  // The full entity diagram is about 60,000 characters, past the 50,000 that pele allows by default.
  const SOURCE_LIMIT = 250_000;
</script>

<script lang="ts">
  let { source }: { source: string } = $props();

  let frame: HTMLDivElement;
  let canvas: HTMLDivElement;
  let phase = $state<'pending' | 'rendered' | 'error'>('pending');
  let message = $state('Loading diagram…');
  let fullscreen = $state(false);

  $effect(() => {
    let handle: { destroy?: () => void } | void;
    let disposed = false;

    const observer = new IntersectionObserver(
      (entries) => {
        if (!entries.some((entry) => entry.isIntersecting)) return;
        observer.disconnect();

        loadPele().then(
          ({ mount }) => {
            if (disposed) return;
            phase = 'rendered';
            handle = mount(canvas, source, {
              idPrefix: `m${Math.random().toString(36).slice(2)}-`,
              // Labels are measured in the font of the page, so the boxes fit the text Scalar draws.
              fontFamily: getComputedStyle(canvas).fontFamily,
              limit: SOURCE_LIMIT,
              onError: () => {
                phase = 'error';
                message = 'This diagram could not be drawn.';
              },
            });
          },
          (error: Error) => {
            phase = 'error';
            message = `Diagram failed to load: ${error.message}`;
          },
        );
      },
      { rootMargin: '200px' },
    );

    observer.observe(frame);
    return () => {
      disposed = true;
      observer.disconnect();
      handle?.destroy?.();
    };
  });

  async function toggleFullscreen() {
    try {
      if (document.fullscreenElement === frame) await document.exitFullscreen();
      else await frame.requestFullscreen();
    } catch (error) {
      console.warn('Fullscreen failed:', error);
    }
  }
</script>

<div class="frame" bind:this={frame} onfullscreenchange={() => (fullscreen = document.fullscreenElement === frame)}>
  <button type="button" onclick={toggleFullscreen}>{fullscreen ? 'Exit full screen' : 'Full screen'}</button>
  {#if phase !== 'rendered'}<p class="status" role="status">{message}</p>{/if}
  <!-- The diagram library owns the contents of this element; Svelte renders nothing into it. -->
  <div class="canvas" bind:this={canvas}></div>
</div>

<style>
  .frame {
    position: relative;
    margin: 1rem 0;
    padding: 2.5rem 1rem 1rem;
    border: var(--scalar-border-width, 1px) solid var(--scalar-border-color, rgba(127, 127, 127, 0.3));
    border-radius: var(--scalar-radius, 6px);
    background: var(--scalar-background-1, transparent);
    --pele-bg: var(--scalar-background-1);
    --pele-fg: var(--scalar-color-1);
    --pele-muted: var(--scalar-color-3);
    --pele-line: var(--scalar-color-2);
    --pele-surface: var(--scalar-background-2);
    --pele-surface-alt: var(--scalar-background-3);
    --pele-border: var(--scalar-color-3);
    --pele-accent: var(--scalar-color-accent);
    --pele-critical: var(--scalar-color-red);
    --pele-error: var(--scalar-color-red);
  }
  .frame:fullscreen {
    display: flex;
    flex-direction: column;
    justify-content: center;
    margin: 0;
    border: none;
    border-radius: 0;
    overflow: auto;
    background: var(--scalar-background-1, #111);
  }
  .canvas {
    display: block;
  }
  .status {
    display: flex;
    align-items: center;
    justify-content: center;
    min-height: 120px;
    margin: 0;
    color: var(--scalar-color-2, #888);
    font-size: 0.85em;
  }
  button {
    position: absolute;
    top: 8px;
    right: 8px;
    z-index: 1;
    padding: 4px 8px;
    font-size: 0.75em;
    background: var(--scalar-background-2, rgba(0, 0, 0, 0.6));
    color: var(--scalar-color-1, #fff);
    border: var(--scalar-border-width, 1px) solid var(--scalar-border-color, rgba(127, 127, 127, 0.3));
    border-radius: 4px;
    cursor: pointer;
  }
</style>
