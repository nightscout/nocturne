import { mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, describe, expect, it } from "vitest";
import { RenderPool, RenderPoolBusyError } from "./pool.js";

/** A worker that answers each job after `job.ms` with the bytes of `job.text`, or stops when told to. */
const script = join(mkdtempSync(join(tmpdir(), "render-pool-")), "worker.mjs");
writeFileSync(
  script,
  `import { parentPort } from "node:worker_threads";
   parentPort.on("message", ({ id, job }) => {
     if (job.exit) process.exit(1);
     if (job.fail) return parentPort.postMessage({ id, error: "drawing failed" });
     setTimeout(() => parentPort.postMessage({ id, png: new TextEncoder().encode(job.text) }), job.ms ?? 0);
   });`,
);

type Job = { text?: string; ms?: number; fail?: boolean; exit?: boolean };
let pool: RenderPool<Job>;

afterEach(async () => {
  await pool.close();
});

const text = (png: Uint8Array) => new TextDecoder().decode(png);

describe("the render pool", () => {
  it("draws each job in a worker and answers its bytes", async () => {
    pool = new RenderPool<Job>(script, { size: 2, queueLimit: 2, timeoutMs: 5000 });

    const answers = await Promise.all(["a", "b", "c"].map((t) => pool.run({ text: t, ms: 20 })));

    expect(answers.map(text)).toEqual(["a", "b", "c"]);
  });

  it("refuses a job as busy when every worker is drawing and the queue is full", async () => {
    pool = new RenderPool<Job>(script, { size: 1, queueLimit: 1, timeoutMs: 5000 });
    const drawing = pool.run({ text: "first", ms: 300 });
    const waiting = pool.run({ text: "second", ms: 10 });

    await expect(pool.run({ text: "third" })).rejects.toBeInstanceOf(RenderPoolBusyError);
    expect(text(await drawing)).toBe("first");
    expect(text(await waiting)).toBe("second");
    expect(text(await pool.run({ text: "after" }))).toBe("after");
  });

  it("ends a job that takes too long and draws the next in a new worker", async () => {
    pool = new RenderPool<Job>(script, { size: 1, queueLimit: 1, timeoutMs: 100 });

    await expect(pool.run({ text: "slow", ms: 2000 })).rejects.toThrow("longer than 100 ms");
    expect(text(await pool.run({ text: "next" }))).toBe("next");
  });

  it("passes on a worker's failure, and replaces a worker that stops", async () => {
    pool = new RenderPool<Job>(script, { size: 1, queueLimit: 1, timeoutMs: 5000 });

    await expect(pool.run({ fail: true })).rejects.toThrow("drawing failed");
    await expect(pool.run({ exit: true })).rejects.toThrow();
    expect(text(await pool.run({ text: "again" }))).toBe("again");
  });
});
