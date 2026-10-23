import { describe, expect, it } from 'vitest';
import { NEAR_VIEWPORT_MARGIN, waitNearViewport } from './viewport';

function fakeObserver() {
  const observed = new Set<Element>();
  let report: ((entries: Array<{ target: Element; isIntersecting: boolean }>) => void) | undefined;
  let margin: string | undefined;
  class FakeObserver {
    constructor(callback: typeof report, options: { rootMargin?: string }) {
      report = callback;
      margin = options.rootMargin;
    }
    observe(el: Element) {
      observed.add(el);
    }
    unobserve(el: Element) {
      observed.delete(el);
    }
  }
  return {
    ctor: FakeObserver as unknown as typeof IntersectionObserver,
    observed,
    get margin() {
      return margin;
    },
    report(target: Element, isIntersecting: boolean) {
      report!([{ target, isIntersecting }]);
    },
  };
}

const element = () => ({ isConnected: true }) as unknown as Element;
const settled = async (promise: Promise<void>) => {
  let done = false;
  void promise.then(() => (done = true));
  await Promise.resolve();
  await Promise.resolve();
  return done;
};

describe('waitNearViewport', () => {
  it('waits until the element comes within the margin, then stops watching it', async () => {
    const io = fakeObserver();
    const el = element();
    const wait = waitNearViewport(el, io.ctor);
    expect(io.margin).toBe(NEAR_VIEWPORT_MARGIN);

    io.report(el, false);
    expect(await settled(wait.ready)).toBe(false);

    io.report(el, true);
    expect(await settled(wait.ready)).toBe(true);
    expect(io.observed.has(el)).toBe(false);
  });

  it('resolves at once with no observer, or for an element outside the document', async () => {
    expect(await settled(waitNearViewport(element(), undefined).ready)).toBe(true);
    const io = fakeObserver();
    const detached = { isConnected: false } as unknown as Element;
    expect(await settled(waitNearViewport(detached, io.ctor).ready)).toBe(true);
    expect(io.observed.size).toBe(0);
  });

  it('lets a cancelled wait go without dropping another waiting on the same element', async () => {
    const io = fakeObserver();
    const el = element();
    const first = waitNearViewport(el, io.ctor);
    const second = waitNearViewport(el, io.ctor);

    first.cancel();
    expect(await settled(first.ready)).toBe(true);
    expect(io.observed.has(el)).toBe(true);

    second.cancel();
    expect(io.observed.has(el)).toBe(false);
  });
});
