import type { Component } from "svelte";

// eslint-disable-next-line @typescript-eslint/no-explicit-any -- any props, exports and bindings; the loaded component keeps its own
type AnyComponent = Component<any, any, any>;

export interface LazyComponent<C extends AnyComponent> {
  /** Whether the component is wanted open. Bind the rendered component's `open` to it. */
  open: boolean;
  /** The loaded component, or null until the first import resolves. */
  readonly component: C | null;
}

/**
 * Imports a component the first time `open` turns true and keeps it loaded, so a dialog rendered
 * from it keeps its close transition. Nothing renders while the chunk loads.
 *
 * A rejected import is logged and drops `open` back to false, so the next open retries it.
 *
 * Call during component initialization: the import runs from an `$effect`.
 */
export function lazyComponent<C extends AnyComponent>(
  loader: () => Promise<{ default: C }>,
): LazyComponent<C> {
  let open = $state(false);
  let component = $state.raw<C | null>(null);
  let loading = false;
  let cancelled = false;

  // Tracks nothing, so its teardown runs only when the owner is destroyed.
  $effect(() => () => {
    cancelled = true;
  });

  $effect(() => {
    if (!open || component || loading) return;
    loading = true;
    loader().then(
      (module) => {
        loading = false;
        if (!cancelled) component = module.default;
      },
      (error: unknown) => {
        loading = false;
        if (cancelled) return;
        console.error("[lazyComponent] Failed to load component", error);
        open = false;
      },
    );
  });

  return {
    get open() {
      return open;
    },
    set open(value: boolean) {
      open = value;
    },
    get component() {
      return component;
    },
  };
}
