import { describe, expect, it } from 'vitest';
import { FIRST_SLICE_TICKS, MAX_FRAME_SECONDS, MAX_SLICE_TICKS, Scheduler, type SchedulerEnv, SlicePacer, frameBudgetMs, sliceTicks } from './scheduler';

type Listener = () => void;

function fakeEnv() {
  let nextHandle = 1;
  const pending = new Map<number, (time: number) => void>();
  let clock = 0;
  let visibilityState = 'visible';
  const listeners = new Set<Listener>();
  const env: SchedulerEnv = {
    requestAnimationFrame: (cb) => {
      const handle = nextHandle++;
      pending.set(handle, cb);
      return handle;
    },
    cancelAnimationFrame: (handle) => {
      pending.delete(handle);
    },
    now: () => clock,
    document: {
      get visibilityState() {
        return visibilityState;
      },
      addEventListener: (_type, listener) => {
        listeners.add(listener);
      },
      removeEventListener: (_type, listener) => {
        listeners.delete(listener);
      },
    },
  };
  return {
    env,
    /** Runs every queued frame callback once at `clock + advanceMs`. */
    frame(advanceMs = 16) {
      clock += advanceMs;
      const callbacks = Array.from(pending.values());
      pending.clear();
      for (const cb of callbacks) cb(clock);
    },
    get queued() {
      return pending.size;
    },
    setVisibility(state: 'visible' | 'hidden') {
      visibilityState = state;
      for (const l of Array.from(listeners)) l();
    },
  };
}

function target() {
  const ticks: number[] = [];
  let renders = 0;
  return {
    ticks,
    get renders() {
      return renders;
    },
    target: {
      tick: (dt: number) => {
        ticks.push(dt);
      },
      render: () => {
        renders += 1;
      },
    },
  };
}

describe('Scheduler', () => {
  it('does not run until an instance is active, and stops when none is', () => {
    const fake = fakeEnv();
    const scheduler = new Scheduler(fake.env);
    const t = target();
    const handle = scheduler.register(t.target);
    expect(scheduler.running).toBe(false);
    expect(fake.queued).toBe(0);

    handle.setActive(true);
    expect(scheduler.running).toBe(true);
    fake.frame();
    fake.frame();
    expect(t.ticks).toEqual([0, 0.016]);
    expect(t.renders).toBe(2);

    handle.setActive(false);
    fake.frame();
    expect(scheduler.running).toBe(false);
    expect(fake.queued).toBe(0);
    expect(t.renders).toBe(2);
  });

  it('pauses while hidden and resumes without counting hidden time', () => {
    const fake = fakeEnv();
    const scheduler = new Scheduler(fake.env);
    const t = target();
    scheduler.register(t.target).setActive(true);
    fake.frame();
    fake.frame();
    expect(t.ticks).toHaveLength(2);

    fake.setVisibility('hidden');
    expect(scheduler.running).toBe(false);
    fake.frame(5000);
    expect(t.ticks).toHaveLength(2);

    fake.setVisibility('visible');
    expect(scheduler.running).toBe(true);
    fake.frame(16);
    fake.frame(16);
    expect(t.ticks).toHaveLength(4);
    expect(t.ticks[2]).toBe(0);
    expect(t.ticks[3]).toBeCloseTo(0.016);
  });

  it('clamps a long stall to MAX_FRAME_SECONDS', () => {
    const fake = fakeEnv();
    const scheduler = new Scheduler(fake.env);
    const t = target();
    scheduler.register(t.target).setActive(true);
    fake.frame();
    fake.frame(2000);
    expect(t.ticks[1]).toBe(MAX_FRAME_SECONDS);
  });

  it('shares one loop between instances and drops disposed ones', () => {
    const fake = fakeEnv();
    const scheduler = new Scheduler(fake.env);
    const a = target();
    const b = target();
    const ha = scheduler.register(a.target);
    const hb = scheduler.register(b.target);
    ha.setActive(true);
    hb.setActive(true);
    fake.frame();
    expect(fake.queued).toBe(1);
    expect(a.renders).toBe(1);
    expect(b.renders).toBe(1);
    hb.dispose();
    fake.frame();
    expect(a.renders).toBe(2);
    expect(b.renders).toBe(1);
    expect(scheduler.stats().frames).toBe(2);
  });

  it('keeps a single loop when an instance re-activates itself from inside its tick', () => {
    const fake = fakeEnv();
    const scheduler = new Scheduler(fake.env);
    let renders = 0;
    const handle = scheduler.register({
      tick: () => handle.setActive(true),
      render: () => {
        renders += 1;
      },
    });
    handle.setActive(true);
    for (let i = 0; i < 10; i++) {
      fake.frame();
      expect(fake.queued).toBe(1);
    }
    expect(renders).toBe(10);
  });

  it('gates a registered element on intersection', () => {
    const fake = fakeEnv();
    let callback: ((records: { isIntersecting: boolean }[]) => void) | undefined;
    class FakeObserver {
      constructor(cb: (records: { isIntersecting: boolean }[]) => void) {
        callback = cb;
      }
      observe() {}
      disconnect() {}
    }
    const scheduler = new Scheduler({ ...fake.env, IntersectionObserver: FakeObserver as unknown as typeof IntersectionObserver });
    const t = target();
    const visibility: boolean[] = [];
    const handle = scheduler.register({
      ...t.target,
      element: {} as Element,
      onVisibilityChange: (v) => visibility.push(v),
    });
    handle.setActive(true);
    callback?.([{ isIntersecting: false }]);
    fake.frame();
    expect(t.renders).toBe(0);
    expect(scheduler.running).toBe(false);
    callback?.([{ isIntersecting: true }]);
    expect(scheduler.running).toBe(true);
    fake.frame();
    expect(t.renders).toBe(1);
    expect(visibility).toEqual([false, true]);
  });

  it('estimates the frame interval from the spacing of frames, discounting late ones', () => {
    const fake = fakeEnv();
    const scheduler = new Scheduler(fake.env);
    expect(scheduler.frameIntervalMs).toBeCloseTo(1000 / 60);
    scheduler.register(target().target).setActive(true);
    for (let i = 0; i < 12; i++) fake.frame(i % 4 === 3 ? 25 : 8.3);
    expect(scheduler.frameIntervalMs).toBeCloseTo(8.3);
    expect(scheduler.frameBudgetMs).toBeCloseTo(4.98);
  });

  it('does not count the time the loop was stopped as a frame', () => {
    const fake = fakeEnv();
    const scheduler = new Scheduler(fake.env);
    const handle = scheduler.register(target().target);
    handle.setActive(true);
    fake.frame(16.7);
    fake.frame(16.7);
    handle.setActive(false);
    fake.frame(16.7);
    handle.setActive(true);
    fake.frame(5000);
    expect(scheduler.frameIntervalMs).toBeCloseTo(16.7);
  });
});

describe('frameBudgetMs', () => {
  it('takes most of a frame, within bounds', () => {
    expect(frameBudgetMs(1000 / 60)).toBeCloseTo(10);
    expect(frameBudgetMs(1000 / 120)).toBe(5);
    expect(frameBudgetMs(1000 / 240)).toBe(4);
    expect(frameBudgetMs(1000 / 30)).toBe(12);
  });
});

describe('sliceTicks', () => {
  it('fits one call to the budget at the measured rate', () => {
    expect(sliceTicks(10, undefined)).toBe(FIRST_SLICE_TICKS);
    expect(sliceTicks(10, 0.5)).toBe(20);
    expect(sliceTicks(10, 0.001)).toBe(MAX_SLICE_TICKS);
    expect(sliceTicks(10, 0)).toBe(MAX_SLICE_TICKS);
    expect(sliceTicks(10, 25)).toBe(0);
    expect(sliceTicks(0, 1)).toBe(0);
  });
});

describe('SlicePacer', () => {
  it('shrinks the next call after a slow one', () => {
    const pacer = new SlicePacer();
    expect(pacer.next(10, 10)).toBe(4);
    pacer.record(4, 20);
    expect(pacer.next(10, 10)).toBe(2);
    pacer.record(2, 100);
    expect(pacer.next(10, 10)).toBe(0);
  });

  it('grows the next call after a fast one', () => {
    const pacer = new SlicePacer();
    pacer.record(4, 2);
    expect(pacer.next(10, 10)).toBe(20);
  });

  it('sizes from the mean of recent calls, so cheap calls between submits do not hide them', () => {
    const pacer = new SlicePacer();
    for (let i = 0; i < 3; i++) pacer.record(4, 0.2);
    expect(pacer.next(10, 10)).toBe(64);
    pacer.record(4, 12.8);
    // About 1 ms a tick over the four calls, where the cheap ones alone said 0.05 ms.
    expect(pacer.next(10, 10)).toBe(10);
  });

  it('caps the ticks a frame queues by their GPU time', () => {
    const pacer = new SlicePacer();
    pacer.record(4, 0.4);
    expect(pacer.next(10, 10, 0.5)).toBe(16);
    pacer.record(16, 1.6);
    expect(pacer.next(8, 10, 0.5)).toBe(0);
    pacer.beginFrame();
    expect(pacer.next(10, 10, 0.5)).toBe(20);
    expect(pacer.next(10, 10, 50)).toBe(1);
  });
});
