// The report image worker: draws one panel per message, off the web app's event loop. Built on its own
// (vite.report-image-worker.config.ts) into build/report-image-worker/worker.mjs; see pool.ts.
import { parentPort } from "node:worker_threads";
import { drawPanel, type DrawJob } from "./draw.js";

parentPort?.on("message", ({ id, job }: { id: number; job: DrawJob }) => {
  try {
    // A copy in an ArrayBuffer of its own: the canvas's buffer is external memory, which cannot be transferred.
    const png = new Uint8Array(drawPanel(job));
    parentPort?.postMessage({ id, png }, [png.buffer]);
  } catch (err) {
    parentPort?.postMessage({ id, error: err instanceof Error ? err.message : String(err) });
  }
});
