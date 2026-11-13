import { Worker } from "node:worker_threads";

/** Every worker is drawing and the queue is full; the caller answers 503. */
export class RenderPoolBusyError extends Error {
  constructor() {
    super("The image renderer is busy.");
  }
}

interface Pending<J> {
  job: J;
  resolve: (png: Uint8Array) => void;
  reject: (err: Error) => void;
}

interface Slot {
  worker: Worker;
  busy: boolean;
}

export interface RenderPoolOptions {
  /** Workers drawing at once. */
  size: number;
  /** Jobs that may wait for a worker; one more is refused. */
  queueLimit: number;
  /** The longest one job may take; its worker is then ended and replaced. */
  timeoutMs: number;
}

/**
 * A fixed set of worker threads, each drawing one job at a time, with a bounded queue. A canvas render
 * is synchronous and can take seconds, so it runs here rather than on the web app's event loop, and
 * the queue bounds how much waits in memory.
 */
export class RenderPool<J> {
  private readonly slots: Slot[] = [];
  private readonly queue: Pending<J>[] = [];
  private nextId = 0;

  constructor(
    private readonly script: URL | string,
    private readonly options: RenderPoolOptions,
  ) {}

  /** The job's PNG; refuses with RenderPoolBusyError when every worker is drawing and the queue is full. */
  run(job: J): Promise<Uint8Array> {
    if (this.queue.length >= this.options.queueLimit && !this.idleSlot())
      return Promise.reject(new RenderPoolBusyError());
    return new Promise((resolve, reject) => {
      this.queue.push({ job, resolve, reject });
      this.pump();
    });
  }

  /** Ends every worker; jobs waiting are refused. */
  async close(): Promise<void> {
    for (const pending of this.queue.splice(0)) pending.reject(new RenderPoolBusyError());
    await Promise.all(this.slots.splice(0).map((slot) => slot.worker.terminate()));
  }

  private idleSlot(): Slot | undefined {
    const idle = this.slots.find((slot) => !slot.busy);
    if (idle) return idle;
    if (this.slots.length < this.options.size) {
      const slot = { worker: new Worker(this.script), busy: false };
      slot.worker.unref();
      this.slots.push(slot);
      return slot;
    }
    return undefined;
  }

  private pump() {
    for (;;) {
      if (this.queue.length === 0) return;
      const slot = this.idleSlot();
      if (!slot) return;
      const pending = this.queue.shift();
      if (pending) this.start(slot, pending);
    }
  }

  private start(slot: Slot, pending: Pending<J>) {
    slot.busy = true;
    const id = this.nextId++;
    const settle = (outcome: () => void) => {
      clearTimeout(timer);
      slot.worker.off("message", onMessage);
      slot.worker.off("error", onFailure);
      slot.worker.off("exit", onFailure);
      outcome();
      slot.busy = false;
      this.pump();
    };
    const replace = () => {
      void slot.worker.terminate();
      this.slots.splice(this.slots.indexOf(slot), 1);
    };
    const onMessage = (message: { id: number; png?: Uint8Array; error?: string }) => {
      if (message.id !== id) return;
      settle(() => (message.png ? pending.resolve(message.png) : pending.reject(new Error(message.error ?? "The render failed."))));
    };
    const onFailure = (err: unknown) => {
      replace();
      settle(() => pending.reject(err instanceof Error ? err : new Error("The render worker stopped.")));
    };
    const timer = setTimeout(() => {
      replace();
      settle(() => pending.reject(new Error(`The render took longer than ${this.options.timeoutMs} ms.`)));
    }, this.options.timeoutMs);
    slot.worker.on("message", onMessage);
    slot.worker.once("error", onFailure);
    slot.worker.once("exit", onFailure);
    slot.worker.postMessage({ id, job: pending.job });
  }
}
