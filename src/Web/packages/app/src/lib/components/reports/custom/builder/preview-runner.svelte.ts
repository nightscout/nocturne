import type { CustomReportRunResponse } from "#lib/api/generated/nocturne-api-client.js";

/**
 * Runs the builder's spec for its preview: a change waits `delay` ms for the next, and at most one run
 * is in flight. A spec that changes while a run is in flight runs once that run ends, and only the
 * latest; a spec the last run already ran is not sent again. `ran` is the JSON of the spec `run`
 * answers, so a page shows a result only beside the spec it belongs to.
 */
export class PreviewRunner {
  run = $state<CustomReportRunResponse | null>(null);
  ran = $state<string | null>(null);
  running = $state(false);
  failure = $state<unknown>(null);

  readonly #send: (spec: unknown) => Promise<CustomReportRunResponse>;
  readonly #delay: number;
  #timer: ReturnType<typeof setTimeout> | undefined;
  #pending: string | null = null;
  #disposed = false;

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

  /**
   * Stops: no waiting or queued spec starts, and the answer of a run in flight is dropped. A remote
   * command has no cancel, so that request still ends on the server.
   */
  dispose() {
    this.#disposed = true;
    clearTimeout(this.#timer);
    this.#pending = null;
  }

  async #start(text: string) {
    if (this.#disposed) return;
    if (text === this.ran && this.run) {
      // Back on the spec the result on screen belongs to: a failure of another spec is not this one's.
      this.failure = null;
      return;
    }
    if (this.running) {
      this.#pending = text;
      return;
    }
    this.running = true;
    try {
      const answer = await this.#send(JSON.parse(text));
      if (this.#disposed) return;
      this.run = answer;
      this.ran = text;
      this.failure = null;
    } catch (err) {
      if (!this.#disposed) this.failure = err;
    } finally {
      this.running = false;
      const next = this.#pending;
      this.#pending = null;
      if (next !== null && next !== text) void this.#start(next);
    }
  }
}
