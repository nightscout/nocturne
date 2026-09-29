<script lang="ts">
  import { untrack, type Snippet } from 'svelte';
  import { prefersReducedMotion } from '../api/capabilities';
  import { resolveMotion } from '../api/mode';
  import { SURFACE_TINT_ALPHA, surfaceTint } from '../api/drop-colour';
  import { measureObstacles, type DropFonts } from '../api/drop-text';
  import { fitMark, type Box, type DropKind, type DropMark } from '../api/drop-stroke';
  import { brushworkMs, dropScene, strokeFrame, type DropDeposit } from '../api/drop-scene';
  import {
    DEFAULT_INTENSITY,
    DEFAULT_REVEAL_MS,
    DEFAULT_TAIL,
    REVEAL_SETTLE_RATIO,
    seedFromName,
    type ArtworkMotion,
    type PaletteId,
    type Surface,
  } from '../types';
  import { createArtworkPlayer, type ArtworkPlayer } from '../api/playback';
  import { getDropGroup } from './drop-group';
  import { applyCanvasFit, componentDpr, hostSurface, watchSurface } from './helpers';

  /**
   * One generated brush stroke on a surface, laid down while the surface is
   * open.
   *
   * Live is what the marks are for: the paint is simulated at the size it is
   * drawn, so its dried edge belongs to that size rather than being a fixed
   * still scaled up, and the palette is exact instead of pushed through a
   * filter. There is no baked or still fallback for a generated stroke. A host
   * that wants the marks calls `getEngineHost().warm()` so the first pointer
   * does not pay the engine boot; a machine with no WebGPU, or one past the
   * live cap, shows nothing.
   */
  let {
    as = 'div',
    index,
    name = '',
    trigger = 'hover',
    selected = false,
    shown,
    peak = 1,
    fonts,
    motion = 'auto',
    palette,
    tintSurface = false,
    progress,
    revealMs = DEFAULT_REVEAL_MS,
    exitMs = 180,
    generation = 0,
    surface,
    deposit = 'wet',
    kind = 'auto',
    spatter = true,
    intensity = DEFAULT_INTENSITY,
    onresolved,
    class: className = '',
    contentClass = '',
    children,
    ...rest
  }: {
    /**
     * The tag the surface renders as.
     *
     * A surface has to BE the link or the button it decorates, not wrap one.
     * A mark inside a host that paints its own background disappears behind
     * it. Anything else passed is spread onto that element, so `href`, `type`
     * and `onclick` go where they belong.
     */
    as?: string;
    /** Position in the run. Claimed from the enclosing `DropGroup` when unset. */
    index?: number;
    /** Seeds the stroke, so one surface paints the same way across renders. */
    name?: string;
    /** What brings the mark in. Each is a hitbox the user points at on purpose. */
    trigger?: 'hover' | 'select' | 'focus' | 'always';
    /** Only for `trigger="select"`. */
    selected?: boolean;
    /** Overrides the trigger outright, for a host driving the surfaces itself. */
    shown?: boolean;
    /** Scales the mark's opacity. */
    peak?: number;
    /** Fonts for the off-DOM measurement; without it every line is measured in the DOM. */
    fonts?: DropFonts;
    motion?: ArtworkMotion;
    /** Paints in this palette instead of the surface's own. */
    palette?: PaletteId;
    /** Warms the surface itself toward the paint that landed on it. */
    tintSurface?: boolean;
    /**
     * Pins the stroke at this point of its settle, 0 to 1, with the transition
     * off. It is how two reveals are compared at the same instant.
     */
    progress?: number;
    revealMs?: number;
    exitMs?: number;
    /** Bumped to repaint with a fresh seed. */
    generation?: number;
    surface?: Surface;
    /** How the stroke is laid down; see `DropDeposit`. */
    deposit?: DropDeposit;
    /**
     * The gesture: a tapered stroke, two or three round drops, one splotch
     * spilling off the top and bottom, a border round the perimeter, a wash
     * over the surface lifted back out of its copy, or a glaze laid thin over
     * the whole control, label included, for a surface that is all label.
     * `auto` cycles the first four along a run, so a grid reads as a
     * composition rather than one effect stamped down it.
     */
    kind?: DropKind | 'auto';
    /** Droplets flicked off the fat end where there is room. */
    spatter?: boolean;
    /** 0..1, scales pigment concentration. */
    intensity?: number;
    onresolved?: (key: string, value: string) => void;
    /** The surface's own chrome: border, background, radius. */
    class?: string;
    /** The content's layout. It sits above the paint, so it cannot be the same element. */
    contentClass?: string;
    children: Snippet;
    [key: string]: unknown;
  } = $props();

  /**
   * How much of the stroke's opacity survives on a dark ground.
   *
   * A dark surface takes the luminous compositing, whose alpha saturates: the
   * same deposit that is a translucent wash on white dries to an opaque pale
   * bar on black, whatever the concentration. Judged by eye against the same
   * stroke on both grounds.
   */
  const DARK_PEAK = 0.45;


  const group = getDropGroup();
  // A slot is claimed once, at init: a member that renumbered itself mid-run
  // would repaint the whole run every time one of its siblings moved.
  const slot = untrack(() => index) ?? group?.claim() ?? 0;
  const groupSeed = group?.seed ?? 0;

  let host: HTMLElement | undefined = $state();
  let content: HTMLElement | undefined = $state();
  let canvas: HTMLCanvasElement | undefined = $state();
  let stroke = $state<DropMark | null>(null);
  let frame = $state<Box | undefined>(undefined);
  let player = $state<ArtworkPlayer>();
  let hovered = $state(false);
  let focused = $state(false);
  /**
   * Set when a pointer that cannot hover opened the surface.
   *
   * A tap fires `pointerenter` and `pointerleave` inside the same gesture, so
   * a hover trigger lights and goes dark before anything can be seen - on a
   * phone the marks were never visible at all. A touch therefore leaves the
   * marks up, and the next pointer that lands elsewhere takes them down.
   */
  let stuck = $state(false);
  /** Kept mounted through the exit, so the paint fades rather than vanishes. */
  let lingering = $state(false);
  /** The backend the player settled on; only a live one draws a generated stroke. */
  let drew = $state<string | undefined>(undefined);
  let theme = $state<Surface>('light');
  /**
   * Whether the surface is near enough to the viewport to be worth painting.
   *
   * Nothing below the fold can be hovered, so a surface down there has no use
   * for a placement or a canvas. It starts true only where there is no
   * observer to ask.
   */
  let visible = $state(typeof IntersectionObserver === 'undefined');

  /** The box the stroke was fitted to, kept because the scene is built from it. */
  let w = 0;
  let h = 0;
  let seed = 0;

  /** A pinned surface is not animating: the host is driving the clock. */
  const scrubbed = $derived(progress !== undefined);
  const animated = $derived(!scrubbed && resolveMotion(motion, prefersReducedMotion()) === 'full');
  const open = $derived(
    scrubbed ||
      (shown ??
        (trigger === 'always'
          ? true
          : trigger === 'select'
            ? selected
            : trigger === 'focus'
              ? focused
              : hovered || focused)),
  );

  /**
   * How far ahead of the viewport a surface prepares itself.
   *
   * Far enough that the stroke is fitted before the surface can be pointed at,
   * and near enough that a long list is not preparing rows nobody will reach.
   */
  const PREPARE_MARGIN = '250px';

  $effect(() => {
    const el = host;
    if (!el || typeof IntersectionObserver === 'undefined') return;
    const observer = new IntersectionObserver(
      (entries) => {
        visible = entries[entries.length - 1]!.isIntersecting;
      },
      { rootMargin: PREPARE_MARGIN },
    );
    observer.observe(el);
    return () => observer.disconnect();
  });

  function enter(event: PointerEvent) {
    hovered = true;
    if (event.pointerType !== 'mouse') stuck = true;
  }

  function leave(event: PointerEvent) {
    // A touch has no leave worth honouring: the one it sends arrives mid-tap.
    if (event.pointerType !== 'mouse') return;
    stuck = false;
    hovered = false;
  }

  $effect(() => {
    if (!stuck) return;
    const el = host;
    const dismiss = (event: PointerEvent) => {
      if (el && event.target instanceof Node && el.contains(event.target)) return;
      stuck = false;
      hovered = false;
    };
    // Captured, so a handler that stops propagation cannot leave a mark lit.
    document.addEventListener('pointerdown', dismiss, true);
    return () => document.removeEventListener('pointerdown', dismiss, true);
  });

  $effect(() => {
    theme = surface ?? hostSurface();
    if (surface) return;
    return watchSurface((next) => (theme = next));
  });

  /**
   * What the last fitting was made from.
   *
   * A surface leaving the viewport and returning asks for the same stroke over
   * and over, and the answer cannot have changed: the inputs are the box, the
   * props and the fonts. Re-entry after the first is free. `fonts` is compared
   * by identity, so a host passing a fresh object literal every render opts
   * itself out - pass a constant.
   */
  let placedFrom = '';
  let placedFonts: DropFonts | undefined;

  function measure() {
    const el = content;
    if (!el || !visible) return;
    w = el.offsetWidth;
    h = el.offsetHeight;
    const key = [w, h, generation, kind, theme, spatter].join('|');
    if (key === placedFrom && fonts === placedFonts) return;

    // A glaze covers the label on purpose, so it has nothing to avoid.
    const obstacles = kind === 'glaze' ? [] : measureObstacles(el, fonts);
    seed = (seedFromName(`${name}-${slot}-${generation}`) ^ groupSeed) >>> 0;
    placedFrom = key;
    placedFonts = fonts;
    // The turn walks the run: consecutive members take consecutive gestures
    // and edges, offset by the group so two runs on a page start differently.
    const turn = slot + (groupSeed % 3);
    stroke = fitMark(w, h, obstacles, { kind, seed, turn, spatter });
    frame = stroke ? strokeFrame(stroke, w, h) : undefined;
  }

  /**
   * How long a resize has to settle before the stroke is fitted again.
   *
   * A drag fires the observer every frame, and a fitting costs a fraction of a
   * millisecond per surface - which a run of them turns into dropped frames
   * for as long as the drag lasts. Nobody is reading the marks mid-drag, so
   * the work waits for the size to stop moving.
   */
  const RESIZE_SETTLE_MS = 120;

  $effect(() => {
    const el = content;
    if (!el) return;
    // Read the inputs a fitting depends on, so a repaint re-measures. A
    // surface scrolled into view measures for the first time here.
    void [generation, kind, fonts, theme, visible, spatter];
    measure();
    let box = `${el.offsetWidth}x${el.offsetHeight}`;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const observer = new ResizeObserver(() => {
      // The observer also fires for changes that round to the same box, which
      // would re-fit the stroke for nothing.
      const next = `${el.offsetWidth}x${el.offsetHeight}`;
      if (next === box) return;
      box = next;
      clearTimeout(timer);
      timer = setTimeout(measure, RESIZE_SETTLE_MS);
    });
    observer.observe(el);
    return () => {
      clearTimeout(timer);
      observer.disconnect();
      group?.release(slot);
    };
  });

  $effect(() => {
    if (open) {
      lingering = true;
      return;
    }
    if (!animated) {
      lingering = false;
      return;
    }
    const timer = setTimeout(() => (lingering = false), exitMs + 40);
    return () => clearTimeout(timer);
  });

  /**
   * The clock the live stroke runs on, matched to the settle the baked marks
   * used so the paths still arrive together. Left unset it would take the
   * engine's 3 s default.
   */
  const engineMs = $derived(Math.round(revealMs * REVEAL_SETTLE_RATIO));

  /**
   * Most of the reveal the pen may spend drawing. A row-wide stroke at the
   * pen's pace would take most of the clock and leave the ink no time to
   * settle, so the brushwork is capped and the pen hurries instead.
   */
  const MAX_BRUSHWORK_SHARE = 0.5;

  /**
   * Builds one live player for the mounted canvas, sized to the stroke's frame.
   *
   * A generated stroke has no fallback: the player is asked for live outright,
   * and a machine that cannot give it draws nothing. The effect owns the
   * player's whole life, so the canvas unmounting at the end of the exit takes
   * the instance with it.
   */
  $effect(() => {
    const el = canvas;
    const current = stroke;
    const box = frame;
    if (!el || !current || !box) return;
    const dpr = componentDpr();
    // Read here, not in the scene callback: that runs later inside the lease,
    // where a read is not a dependency, and a palette change would not repaint.
    const options = { palette: palette ?? 'water', surface: theme, seed, intensity, deposit, dpr };
    applyCanvasFit(el, { width: box.w, height: box.h, offsetX: 0, offsetY: 0 }, dpr);
    const next = createArtworkPlayer(
      el,
      { scene: (module) => dropScene(module, current, w, h, options).sceneJson },
      {
        mode: 'live',
        durationMs: engineMs,
        // The pen's share of the clock: what the drawn stroke takes at its
        // pace, and never less than the default brushwork a laydown gets.
        tail:
          1 -
          Math.min(MAX_BRUSHWORK_SHARE, Math.max(1 - DEFAULT_TAIL, brushworkMs(current) / engineMs)),
        motion,
        width: box.w,
        height: box.h,
        dpr,
        // Only a scrubbed surface ever seeks, so only it pays for checkpoints.
        checkpointBudgetBytes: scrubbed ? undefined : 1,
      },
    );
    drew = undefined;
    // Untracked, because the player emits synchronously from `pause` and
    // `seek`, which the scrub effect calls. A host callback that reads and
    // writes its own state inside that effect would make the state a
    // dependency of the effect and re-run it without end.
    const write = () =>
      untrack(() => {
        const { mode: resolved, fallbackReason } = next.state;
        drew = resolved;
        onresolved?.(name || `surface ${slot}`, fallbackReason ? `${resolved} (${fallbackReason})` : resolved);
      });
    const offs = [next.on('ready', write), next.on('statechange', write), next.on('fallback', write)];
    player = next;
    return () => {
      for (const off of offs) off();
      next.dispose();
      player = undefined;
    };
  });

  $effect(() => {
    const current = player;
    if (!current || progress === undefined || drew !== 'live') return;
    current.pause();
    current.seek(progress);
  });

  /**
   * True while a stroke that has arrived is on its way out.
   *
   * Without it the exit would cut the paint off when the canvas unmounted.
   */
  const leaving = $derived(lingering && !open);

  const tintAlpha = $derived(progress ?? (open && drew === 'live' ? 1 : 0));
  const tint = $derived(
    tintSurface ? surfaceTint(palette ?? 'water', SURFACE_TINT_ALPHA * tintAlpha) : 'transparent',
  );
</script>

<!--
  Three layers, because the surface paints its own background: the host draws
  the chrome, the stroke sits above that background, and the content sits above
  the stroke. A mark placed on the host with a negative z-index disappears
  behind the very background it is meant to bleed into.
-->
<svelte:element
  this={as}
  bind:this={host}
  role={as === 'div' ? 'presentation' : undefined}
  {...rest}
  onpointerenter={enter}
  onpointerleave={leave}
  onfocusin={() => (focused = true)}
  onfocusout={() => (focused = false)}
  class="nwc-drops relative isolate overflow-hidden {className}"
  style:--nwc-drop-settle-ms="{engineMs}ms"
  style:--nwc-drop-exit-ms="{exitMs}ms"
>
  {#if tintSurface}
    <div class="nwc-drops__tint pointer-events-none absolute inset-0 z-0" style:background-color={tint}></div>
  {/if}
  <div class="pointer-events-none absolute inset-0 z-0">
    {#if stroke && frame && lingering && visible}
      <div
        class="nwc-drop"
        data-kind={stroke.kind}
        class:nwc-drop--shown={drew === 'live'}
        class:nwc-drop--animated={animated && lingering}
        class:nwc-drop--leaving={leaving}
        style:left="{frame.x}px"
        style:top="{frame.y}px"
        style:width="{frame.w}px"
        style:height="{frame.h}px"
        style:--nwc-drop-peak={peak * (theme === 'dark' ? DARK_PEAK : 1)}
      >
        <canvas bind:this={canvas}></canvas>
      </div>
    {/if}
  </div>
  <div bind:this={content} class="relative z-10 {contentClass}">
    {@render children()}
  </div>
</svelte:element>

<style>
  .nwc-drops__tint {
    transition: background-color var(--nwc-drop-settle-ms) ease-out;
  }

  .nwc-drop {
    position: absolute;
    overflow: hidden;
    opacity: 0;
  }

  .nwc-drop canvas {
    display: block;
  }

  .nwc-drop--shown {
    opacity: var(--nwc-drop-peak);
  }

  .nwc-drop--animated.nwc-drop--shown {
    transition: opacity 160ms ease-out;
    will-change: opacity;
  }

  /*
   * Declared last, so it wins over the arrived state. A stroke leaving fades
   * from where it dried rather than rewinding.
   */
  .nwc-drop--leaving {
    opacity: 0;
  }

  .nwc-drop--animated.nwc-drop--leaving {
    transition: opacity var(--nwc-drop-exit-ms) ease-out;
    will-change: opacity;
  }
</style>
