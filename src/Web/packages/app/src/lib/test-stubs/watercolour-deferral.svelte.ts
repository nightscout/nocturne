/**
 * Stand-in for `#lib/watercolour-deferral.svelte.js` whose first-interaction window passes only when
 * the test calls `releaseWatercolour`, so a test can assert what a page shows before its deferred
 * watercolour and after. Artwork that draws a still is due at once. Install with
 * `vi.mock("#lib/watercolour-deferral.svelte.js", () => import("#lib/test-stubs/watercolour-deferral.svelte.js"))`.
 */
import { watercolourDue } from "#lib/stores/illustrations.svelte.js";
import { lazyComponent, type AnyComponent } from "#lib/utils/lazy-component.svelte.js";

const deferral = $state({ due: false });

export const WATERCOLOUR_DEFER_MS = 5000;

export function deferredWatercolour({ needsEngine = true }: { needsEngine?: boolean } = {}): {
  readonly due: boolean;
} {
  return {
    get due() {
      return watercolourDue(!needsEngine || deferral.due);
    },
  };
}

function lazyArtwork<C extends AnyComponent>(
  loader: () => Promise<{ default: C }>,
  wanted: () => boolean,
  needsEngine: boolean,
): { readonly component: C | null } {
  const lazy = lazyComponent(loader);
  const window = deferredWatercolour({ needsEngine });
  $effect(() => {
    if (window.due && wanted()) lazy.open = true;
  });
  return {
    get component() {
      return lazy.component;
    },
  };
}

export function deferredArtwork<C extends AnyComponent>(
  loader: () => Promise<{ default: C }>,
  wanted: () => boolean = () => true,
): { readonly component: C | null } {
  return lazyArtwork(loader, wanted, true);
}

export function stillArtwork<C extends AnyComponent>(
  loader: () => Promise<{ default: C }>,
  wanted: () => boolean = () => true,
): { readonly component: C | null } {
  return lazyArtwork(loader, wanted, false);
}

export function releaseWatercolour(): void {
  deferral.due = true;
}

export function resetWatercolour(): void {
  deferral.due = false;
}
