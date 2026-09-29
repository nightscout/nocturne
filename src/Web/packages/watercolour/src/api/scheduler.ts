/**
 * One `requestAnimationFrame` loop for every live or baked instance on the
 * page. Hidden time is not counted as elapsed, so a reveal resumes where it
 * paused instead of jumping to the end.
 */

export interface SchedulerEnv {
  requestAnimationFrame(callback: (time: number) => void): number;
  cancelAnimationFrame(handle: number): void;
  now(): number;
  document?: {
    visibilityState: string;
    addEventListener(type: 'visibilitychange', listener: () => void): void;
    removeEventListener(type: 'visibilitychange', listener: () => void): void;
  };
  IntersectionObserver?: typeof IntersectionObserver;
}

export interface SchedulerTarget {
  /** Observed for visibility; omit to treat the target as always visible. */
  element?: Element | null;
  tick(elapsedSeconds: number): void;
  render(): void;
  onVisibilityChange?(visible: boolean): void;
}

export interface SchedulerHandle {
  setActive(active: boolean): void;
  readonly active: boolean;
  readonly visible: boolean;
  dispose(): void;
}

export interface FrameStats {
  frames: number;
  averageMs: number;
  p95Ms: number;
  lastMs: number;
}

/** Longest step handed to an instance; a stalled tab resumes gently instead of jumping. */
export const MAX_FRAME_SECONDS = 0.25;
const HISTOGRAM_SIZE = 120;
const INTERVAL_SAMPLES = 16;
const DEFAULT_FRAME_INTERVAL_MS = 1000 / 60;

/**
 * Main-thread time sliced work may take out of one display frame: enough of
 * it to finish quickly, the rest left to the page and the compositor.
 */
export function frameBudgetMs(frameIntervalMs: number): number {
  return Math.min(12, Math.max(4, frameIntervalMs * 0.6));
}

/** Ticks the first sliced call runs, before any call has been timed. */
export const FIRST_SLICE_TICKS = 4;
/** Most ticks one sliced call runs, which bounds what a stale estimate can cost. */
export const MAX_SLICE_TICKS = 64;

/** Ticks one call can run in `budgetMs` at `msPerTick`; 0 when not even one fits. */
export function sliceTicks(budgetMs: number, msPerTick: number | undefined): number {
  if (msPerTick === undefined) return FIRST_SLICE_TICKS;
  return Math.max(0, Math.min(MAX_SLICE_TICKS, Math.floor(budgetMs / Math.max(msPerTick, 1e-3))));
}

/**
 * Sizes sliced engine calls from how long recent ones took. One call cannot
 * be split, so a fixed tick count that is cheap on a desktop can hold a slow
 * phone's thread for a hundred milliseconds. The estimate is shared: stills
 * run the same engine at similar sizes, so each starts from the last one's rate.
 *
 * A call returns once its GPU work is queued, not done, so the ticks a frame
 * queues are also capped by the GPU time they were last measured at.
 */
export class SlicePacer {
  /**
   * Decaying sums over recent calls, read as a mean per tick. Every few calls
   * one carries the batched GPU submit; sized from the cheap calls between,
   * the next call would fold several submits into one long task.
   */
  private msSum = 0;
  private tickSum = 0;
  private frameTicks = 0;

  beginFrame(): void {
    this.frameTicks = 0;
  }

  /** Ticks for the next call: 0 once the budget left fits none, or this frame's GPU share is queued. */
  next(remainingMs: number, budgetMs: number, gpuTickMs?: number | null): number {
    const ticks = sliceTicks(Math.max(0, remainingMs), this.tickSum > 0 ? this.msSum / this.tickSum : undefined);
    if (!gpuTickMs || gpuTickMs <= 0) return ticks;
    const gpuLeft = Math.floor(budgetMs / gpuTickMs) - this.frameTicks;
    return Math.min(ticks, this.frameTicks === 0 ? Math.max(1, gpuLeft) : Math.max(0, gpuLeft));
  }

  record(ticks: number, ms: number): void {
    this.frameTicks += ticks;
    this.msSum = this.msSum * 0.9 + ms;
    this.tickSum = this.tickSum * 0.9 + ticks;
  }
}

interface Entry {
  target: SchedulerTarget;
  active: boolean;
  visible: boolean;
  observer?: IntersectionObserver;
}

function browserEnv(): SchedulerEnv {
  return {
    requestAnimationFrame: (cb) => requestAnimationFrame(cb),
    cancelAnimationFrame: (h) => cancelAnimationFrame(h),
    now: () => performance.now(),
    document: typeof document === 'undefined' ? undefined : document,
    IntersectionObserver: typeof IntersectionObserver === 'undefined' ? undefined : IntersectionObserver,
  };
}

export class Scheduler {
  private readonly env: SchedulerEnv;
  private readonly entries = new Set<Entry>();
  readonly slices = new SlicePacer();
  private frameHandle: number | undefined;
  /** `setActive(true)` from inside a tick must not queue a second loop; the frame reschedules itself. */
  private inFrame = false;
  private lastTime: number | undefined;
  private frameStartedAt: number | undefined;
  private hidden = false;
  private readonly durations = new Float64Array(HISTOGRAM_SIZE);
  private durationCount = 0;
  private durationIndex = 0;
  private lastDuration = 0;
  private readonly budgetWaiters: Array<() => void> = [];
  private readonly intervals: number[] = [];
  private interval = DEFAULT_FRAME_INTERVAL_MS;
  private readonly onVisibility = () => {
    const state = this.env.document?.visibilityState ?? 'visible';
    this.hidden = state === 'hidden';
    if (this.hidden) {
      this.stopLoop();
    } else {
      this.lastTime = undefined;
      this.ensureLoop();
    }
  };

  constructor(env: Partial<SchedulerEnv> = {}) {
    this.env = { ...browserEnv(), ...env };
    this.hidden = this.env.document?.visibilityState === 'hidden';
    this.env.document?.addEventListener('visibilitychange', this.onVisibility);
  }

  get running(): boolean {
    return this.frameHandle !== undefined;
  }

  now(): number {
    return this.env.now();
  }

  /**
   * The display's frame interval, from the spacing of recent frames. A low
   * percentile rather than the mean: a frame that overran arrives late, and
   * counting it would raise the budget exactly when the page is struggling.
   */
  get frameIntervalMs(): number {
    return this.interval;
  }

  get frameBudgetMs(): number {
    return frameBudgetMs(this.interval);
  }

  /**
   * What the current frame's budget has left, shared by everything sliced in
   * it. It keeps counting after the frame callback returns, so work started in
   * that frame's microtasks (a still handed its turn) spends the same budget.
   */
  budgetRemainingMs(): number {
    if (this.frameStartedAt === undefined) return 0;
    return this.frameBudgetMs - (this.env.now() - this.frameStartedAt);
  }

  /** Resolves now if the current frame has budget left, else once the next frame has run. */
  whenBudget(): Promise<void> {
    if (this.budgetRemainingMs() > 0) return Promise.resolve();
    return new Promise((resolve) => {
      this.budgetWaiters.push(resolve);
      this.ensureLoop();
    });
  }

  register(target: SchedulerTarget): SchedulerHandle {
    const entry: Entry = { target, active: false, visible: true };
    if (target.element && this.env.IntersectionObserver) {
      entry.observer = new this.env.IntersectionObserver((records) => {
        const last = records[records.length - 1];
        if (!last) return;
        const visible = last.isIntersecting;
        if (visible === entry.visible) return;
        entry.visible = visible;
        target.onVisibilityChange?.(visible);
        if (visible) this.ensureLoop();
      });
      entry.observer.observe(target.element);
    }
    this.entries.add(entry);
    const scheduler = this;
    return {
      get active() {
        return entry.active;
      },
      get visible() {
        return entry.visible;
      },
      setActive(active: boolean) {
        entry.active = active;
        if (active) scheduler.ensureLoop();
      },
      dispose() {
        entry.observer?.disconnect();
        scheduler.entries.delete(entry);
      },
    };
  }

  stats(): FrameStats {
    const n = this.durationCount;
    if (n === 0) return { frames: 0, averageMs: 0, p95Ms: 0, lastMs: 0 };
    const sample = Array.from(this.durations.subarray(0, n)).sort((a, b) => a - b);
    const sum = sample.reduce((acc, v) => acc + v, 0);
    return {
      frames: n,
      averageMs: sum / n,
      p95Ms: sample[Math.min(n - 1, Math.floor(n * 0.95))]!,
      lastMs: this.lastDuration,
    };
  }

  dispose(): void {
    this.stopLoop();
    for (const entry of this.entries) entry.observer?.disconnect();
    this.entries.clear();
    this.env.document?.removeEventListener('visibilitychange', this.onVisibility);
  }

  private hasWork(): boolean {
    if (this.budgetWaiters.length > 0) return true;
    for (const entry of this.entries) {
      if (entry.active && entry.visible) return true;
    }
    return false;
  }

  private ensureLoop(): void {
    if (this.hidden || this.inFrame || this.frameHandle !== undefined || !this.hasWork()) return;
    this.frameHandle = this.env.requestAnimationFrame(this.frame);
  }

  private stopLoop(): void {
    if (this.frameHandle !== undefined) {
      this.env.cancelAnimationFrame(this.frameHandle);
      this.frameHandle = undefined;
    }
    this.lastTime = undefined;
  }

  private readonly frame = (time: number) => {
    this.frameHandle = undefined;
    const elapsed = this.lastTime === undefined ? 0 : Math.min(MAX_FRAME_SECONDS, Math.max(0, (time - this.lastTime) / 1000));
    if (this.lastTime !== undefined && time > this.lastTime) this.sampleInterval(time - this.lastTime);
    this.lastTime = time;
    const started = this.env.now();
    this.frameStartedAt = started;
    this.slices.beginFrame();
    for (const resolve of this.budgetWaiters.splice(0)) resolve();
    this.inFrame = true;
    try {
      for (const entry of Array.from(this.entries)) {
        if (!entry.active || !entry.visible) continue;
        entry.target.tick(elapsed);
        entry.target.render();
      }
    } finally {
      this.inFrame = false;
    }
    this.record(this.env.now() - started);
    if (this.hasWork()) {
      this.frameHandle = this.env.requestAnimationFrame(this.frame);
    } else {
      this.lastTime = undefined;
    }
  };

  private sampleInterval(ms: number): void {
    this.intervals.push(ms);
    if (this.intervals.length > INTERVAL_SAMPLES) this.intervals.shift();
    const sorted = [...this.intervals].sort((a, b) => a - b);
    this.interval = sorted[Math.floor((sorted.length - 1) / 4)]!;
  }

  private record(ms: number): void {
    this.lastDuration = ms;
    this.durations[this.durationIndex] = ms;
    this.durationIndex = (this.durationIndex + 1) % HISTOGRAM_SIZE;
    this.durationCount = Math.min(HISTOGRAM_SIZE, this.durationCount + 1);
  }
}

let shared: Scheduler | undefined;

export function getScheduler(): Scheduler {
  shared ??= new Scheduler();
  return shared;
}
