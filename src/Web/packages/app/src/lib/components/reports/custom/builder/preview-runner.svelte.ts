import type { CustomReportRunResponse } from "#lib/api/generated/nocturne-api-client.js";

/**
 * Runs the builder's spec for its preview: a change waits `delay` ms for the next, and at most one run
 * is in flight. A spec that changes while a run is in flight runs once that run ends, and only the
 * latest; a spec the last run already ran is not sent again.
 */
export class PreviewRunner {
  run = $state<CustomReportRunResponse | null>(null);
  running = $state(false);
  failure = $state<unknown>(null);

  readonly #send: (spec: unknown) => Promise<CustomReportRunResponse>;
  readonly #delay: number;
  #timer: ReturnType<typeof setTimeout> | undefined;
  #pending: string | null = null;
  #last: string | null = null;

  constructor(send: (spec: unknown) => Promise<CustomReportRunResponse>, delay = 600) {
    this.#send = send;
    this.#delay = delay;
  }

  /** Runs `spec` after the delay, unless another change comes first. */
  schedule(spec: unknown) {
    const text = JSON.stringify(spec);
    clearTimeout(this.#timer);
    this.#timer = setTimeout(() => void this.#start(text), this.#delay);
  }

  /** Runs `spec` now: the first preview, with nothing to wait for. */
  now(spec: unknown) {
    clearTimeout(this.#timer);
    void this.#start(JSON.stringify(spec));
  }

  dispose() {
    clearTimeout(this.#timer);
  }

  async #start(text: string) {
    if (text === this.#last && this.run) return;
    if (this.running) {
      this.#pending = text;
      return;
    }
    this.running = true;
    try {
      this.run = await this.#send(JSON.parse(text));
      this.#last = text;
      this.failure = null;
    } catch (err) {
      this.failure = err;
    } finally {
      this.running = false;
      const next = this.#pending;
      this.#pending = null;
      if (next !== null && next !== text) void this.#start(next);
    }
  }
}
