import { flushSync, getContext, setContext } from "svelte";

/** How long one frame spends mounting rows and charts, which keeps each frame far below 100 ms. */
export const REVEAL_BUDGET_MS = 30;

const KEY = "nocturne:people:reveal";

/**
 * Spreads the mounting of a long roster over frames. Each frame runs the waiting steps, each applied at once, until
 * {@link REVEAL_BUDGET_MS} is spent; the rest wait for the next frame. A chart is mounted only while its row is near
 * the viewport: each chart lays out the page as it mounts and holds hundreds of nodes, so a roster that held every
 * chart would stall for seconds as it mounts, sorts or changes lens.
 */
export class Reveal {
  #steps: (() => void)[] = [];
  #frame = 0;
  #frameStart = 0;
  #ranThisFrame = 0;
  #yielded = false;
  #sizedWaiting = false;
  #msPerUnit = 0;
  #near = new Map<Element, { near: (isNear: boolean) => void; isNear: boolean }>();
  #observer: IntersectionObserver | undefined;

  /** Runs `step` in the next frame with budget left. */
  schedule(step: () => void): void {
    this.#steps.push(step);
    this.#frame ||= requestAnimationFrame(this.#run);
  }

  /**
   * Runs `step` in a frame with budget left, given how many units to take: `most` until a sized step has run, then as
   * many as fit in the frame's budget left at the pace of the last sized step, between one and `most`. A slow device so
   * takes fewer units a step rather than running far past the budget. A step that would take fewer than `most` after
   * other steps waits for the next frame instead, since every step costs the roster a pass over its rows. At most one
   * sized step waits at a time: a call while one waits is dropped, so `step` reads the state it advances when it runs.
   */
  scheduleSized(most: number, step: (count: number) => void): void {
    if (this.#sizedWaiting) return;
    this.#sizedWaiting = true;
    const sized = () => {
      const left = REVEAL_BUDGET_MS - (performance.now() - this.#frameStart);
      const fit = this.#msPerUnit > 0 ? Math.floor(left / this.#msPerUnit) : most;
      if (fit < most && this.#ranThisFrame > 0) {
        this.#steps.unshift(sized);
        this.#yielded = true;
        return;
      }
      this.#sizedWaiting = false;
      const count = Math.max(1, Math.min(most, fit));
      const start = performance.now();
      step(count);
      flushSync();
      this.#msPerUnit = (performance.now() - start) / count;
    };
    this.schedule(sized);
  }

  /**
   * Calls `near(true)`, in a frame with budget left, when `element` comes within a screen's height of the viewport,
   * and `near(false)` when it leaves, so a sort or lens switch moves only the charts near the viewport. Returns the
   * stop.
   */
  whenNear(element: Element, near: (isNear: boolean) => void): () => void {
    this.#observer ??= new IntersectionObserver(
      (entries) => {
        for (const entry of entries) {
          const watched = this.#near.get(entry.target);
          if (!watched) continue;
          watched.isNear = entry.isIntersecting;
          if (entry.isIntersecting) this.schedule(() => watched.isNear && watched.near(true));
          else watched.near(false);
        }
      },
      { rootMargin: "100% 0px" }
    );
    this.#near.set(element, { near, isNear: false });
    this.#observer.observe(element);
    return () => {
      this.#near.delete(element);
      this.#observer?.unobserve(element);
    };
  }

  destroy(): void {
    cancelAnimationFrame(this.#frame);
    this.#frame = 0;
    this.#steps = [];
    this.#sizedWaiting = false;
    this.#observer?.disconnect();
    this.#near.clear();
  }

  // The frame stays held while the steps run, so a step that schedules another adds it to this run. A step that throws
  // still leaves the next frame asked for, so the rest of the roster mounts.
  #run = () => {
    try {
      this.#frameStart = performance.now();
      this.#ranThisFrame = 0;
      this.#yielded = false;
      while (!this.#yielded && this.#steps.length > 0 && performance.now() - this.#frameStart < REVEAL_BUDGET_MS) {
        this.#steps.shift()?.();
        flushSync();
        this.#ranThisFrame++;
      }
    } finally {
      this.#frame = this.#steps.length > 0 ? requestAnimationFrame(this.#run) : 0;
    }
  };
}

/** Makes `reveal` the one the rows and charts below use. Call during component initialisation. */
export function setReveal(reveal: Reveal): void {
  setContext(KEY, reveal);
}

/** The roster's {@link Reveal}; undefined outside a roster, where everything mounts at once. */
export function getReveal(): Reveal | undefined {
  return getContext<Reveal | undefined>(KEY);
}
