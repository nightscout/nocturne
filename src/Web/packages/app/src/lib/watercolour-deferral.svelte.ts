import { browser } from "$app/env";
import { WARM_IDLE_TIMEOUT_MS } from "water-ui";
import { watercolourDue } from "#lib/stores/illustrations.svelte.js";
import { lazyComponent, type AnyComponent } from "#lib/utils/lazy-component.svelte.js";
import { whenIdle } from "#lib/utils/when-idle.js";

/** Counted from navigation start. */
export const WATERCOLOUR_DEFER_MS = 5000;

export interface DeferredWatercolour {
  /** See {@link watercolourDue}. */
  readonly due: boolean;
}

/**
 * When watercolour the page is not for may load.
 *
 * With `needsEngine` (the default), not before the first-interaction window is over: five seconds
 * from navigation start, then the next idle period. The engine boot stalls frame presentation for
 * most of a second (measured at 800 ms on a desktop GPU), and the first idle after hydration is
 * when the viewer starts to interact. The engine warm-up waits for this, and so does artwork with
 * no still to draw while the engine is cold (a scene wash, a bloom), which would boot it on mount.
 * The window is counted from navigation start, so every caller on a page comes due together, and
 * one created after it is due at once, as on a soft navigation.
 *
 * Without it, at the next idle period: the library draws an artwork's baked still until the engine
 * is ready, so it boots nothing and only its chunk is worth keeping off the hydration path.
 *
 * Call during component initialisation.
 */
export function deferredWatercolour({ needsEngine = true }: { needsEngine?: boolean } = {}): DeferredWatercolour {
  let passed = $state(needsEngine && browser && performance.now() >= WATERCOLOUR_DEFER_MS);

  $effect(() => {
    if (passed) return;
    let cancelIdle: (() => void) | undefined;
    const timer = setTimeout(
      () => {
        cancelIdle = whenIdle(() => (passed = true), { timeout: WARM_IDLE_TIMEOUT_MS });
      },
      needsEngine ? Math.max(0, WATERCOLOUR_DEFER_MS - performance.now()) : 0,
    );
    return () => {
      clearTimeout(timer);
      cancelIdle?.();
    };
  });

  return {
    get due() {
      return watercolourDue(passed);
    },
  };
}

function lazyArtwork<C extends AnyComponent>(
  loader: () => Promise<{ default: C }>,
  wanted: () => boolean,
  needsEngine: boolean,
): { readonly component: C | null } {
  const lazy = lazyComponent(loader);
  const deferral = deferredWatercolour({ needsEngine });
  $effect(() => {
    if (deferral.due && wanted()) lazy.open = true;
  });
  return {
    get component() {
      return lazy.component;
    },
  };
}

/**
 * A decorative watercolour component with no still to draw while the engine is cold, imported
 * once {@link deferredWatercolour} comes due and `wanted` holds, so it neither loads nor boots the
 * engine inside the first-interaction window. Render it with
 * `{#if x.component}<x.component … />{/if}`.
 *
 * Call during component initialisation.
 */
export function deferredArtwork<C extends AnyComponent>(
  loader: () => Promise<{ default: C }>,
  wanted: () => boolean = () => true,
): { readonly component: C | null } {
  return lazyArtwork(loader, wanted, true);
}

/**
 * A decorative watercolour component that draws its baked still while the engine is cold,
 * imported at the next idle period once `wanted` holds; see {@link deferredWatercolour}.
 *
 * Call during component initialisation.
 */
export function stillArtwork<C extends AnyComponent>(
  loader: () => Promise<{ default: C }>,
  wanted: () => boolean = () => true,
): { readonly component: C | null } {
  return lazyArtwork(loader, wanted, false);
}
