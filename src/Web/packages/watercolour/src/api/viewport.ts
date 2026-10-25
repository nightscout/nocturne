/**
 * How far outside the viewport an artwork starts preparing. Far enough that a
 * scroll reaches finished paint, near enough that a long list does not
 * simulate rows nobody will see.
 */
export const NEAR_VIEWPORT_MARGIN = '200px';

export interface ViewportWait {
  readonly ready: Promise<void>;
  /** Resolves `ready` without waiting, for a player disposed first. */
  cancel(): void;
}

type ObserverCtor = typeof IntersectionObserver;

let shared: { ctor: ObserverCtor; observer: IntersectionObserver } | undefined;
const waiting = new Map<Element, Set<() => void>>();

function observer(ctor: ObserverCtor): IntersectionObserver {
  if (shared?.ctor !== ctor) {
    const observer = new ctor(
      (entries) => {
        for (const entry of entries) {
          if (!entry.isIntersecting) continue;
          const resolvers = waiting.get(entry.target);
          waiting.delete(entry.target);
          observer.unobserve(entry.target);
          for (const resolve of resolvers ?? []) resolve();
        }
      },
      { rootMargin: NEAR_VIEWPORT_MARGIN },
    );
    shared = { ctor, observer };
  }
  return shared.observer;
}

/**
 * Resolves once `element` is within {@link NEAR_VIEWPORT_MARGIN} of the
 * viewport. Without an observer, or for an element outside the document,
 * which an observer would never report, it resolves at once.
 */
export function waitNearViewport(
  element: Element,
  ctor: ObserverCtor | undefined = typeof IntersectionObserver === 'undefined' ? undefined : IntersectionObserver,
): ViewportWait {
  if (!ctor || !element.isConnected) return { ready: Promise.resolve(), cancel: () => {} };
  let resolve!: () => void;
  const ready = new Promise<void>((r) => (resolve = r));
  const io = observer(ctor);
  let resolvers = waiting.get(element);
  if (!resolvers) {
    resolvers = new Set();
    waiting.set(element, resolvers);
    io.observe(element);
  }
  resolvers.add(resolve);
  return {
    ready,
    cancel: () => {
      const set = waiting.get(element);
      if (set?.delete(resolve) && set.size === 0) {
        waiting.delete(element);
        io.unobserve(element);
      }
      resolve();
    },
  };
}
