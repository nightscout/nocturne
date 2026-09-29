import type { ArtworkPlayer, PlayerReadyCallback } from '../src';

interface Tracked {
  label: string;
  readyAt: number;
  finishedAt?: number;
  mode: string;
}

/** What run.mjs reads. Times are performance.now() values. */
interface BenchState {
  scenario: string;
  expected: number;
  mountAt: number;
  firstReadyAt?: number;
  readyAt?: number;
  timedOut: boolean;
  tracked: Tracked[];
}

const TIMEOUT_MS = 90_000;

const state: BenchState = { scenario: '', expected: 0, mountAt: 0, timedOut: false, tracked: [] };
let resolveReady: (state: BenchState) => void = () => {};

const w = window as unknown as { __bench: BenchState & { pending(): number }; __benchReady: Promise<BenchState> };
w.__bench = Object.assign(state, { pending: () => state.tracked.filter((t) => t.finishedAt === undefined).length });
w.__benchReady = new Promise<BenchState>((resolve) => (resolveReady = resolve));

export function begin(): void {
  state.mountAt = performance.now();
  performance.mark('bench:mount');
  setTimeout(() => {
    if (state.readyAt !== undefined) return;
    state.timedOut = true;
    state.readyAt = performance.now();
    resolveReady(state);
  }, TIMEOUT_MS);
}

export function expectArtworks(scenario: string, n: number): void {
  state.scenario = scenario;
  state.expected = n;
  if (n === 0) queueMicrotask(check);
}

function check(): void {
  if (state.readyAt !== undefined) return;
  if (state.tracked.length < state.expected) return;
  if (state.tracked.some((t) => t.finishedAt === undefined)) return;
  const last = Math.max(state.mountAt, ...state.tracked.map((t) => t.finishedAt ?? 0));
  state.readyAt = last;
  performance.mark('bench:ready');
  resolveReady(state);
}

/** An `onready` that records when the artwork was drawing and when it settled. */
export function track(label: string): PlayerReadyCallback {
  return (player: ArtworkPlayer) => {
    const entry: Tracked = { label, readyAt: performance.now(), mode: player.state.mode };
    state.tracked.push(entry);
    if (state.firstReadyAt === undefined) {
      state.firstReadyAt = entry.readyAt;
      performance.mark('bench:first-artwork');
    }
    const done = () => {
      if (entry.finishedAt !== undefined) return;
      entry.finishedAt = performance.now();
      entry.mode = player.state.mode;
      check();
    };
    if (player.state.finished) done();
    const off = [player.on('finished', done), player.on('error', done)];
    return () => off.forEach((f) => f());
  };
}
