import { errorStatus } from "#lib/forms/submit-error.js";

const MAX_CONSECUTIVE_FAILURES = 5;

/**
 * Polls a background job's status until it ends. Returns the function that stops it.
 * `gone` is called when the server refuses the job (`refused`: a 403 or 404, so this session cannot
 * see it or the server no longer has it) or does not answer five times in a row.
 */
export function followStatus<T>(options: {
  read: () => Promise<T>;
  isLive: (status: T) => boolean;
  update: (status: T) => void;
  ended: (status: T) => void;
  gone: (refused: boolean) => void;
  intervalMs: number;
}): () => void {
  let active = true;
  let timer: ReturnType<typeof setTimeout> | undefined;
  let failures = 0;

  async function poll() {
    // A hidden tab shows no one the progress, and a job can run for hours.
    if (globalThis.document?.visibilityState === "hidden") {
      timer = setTimeout(poll, options.intervalMs);
      return;
    }

    try {
      const latest = await options.read();
      if (!active) return;
      failures = 0;
      if (!options.isLive(latest)) {
        active = false;
        options.ended(latest);
        return;
      }
      options.update(latest);
    } catch (err) {
      if (!active) return;
      const refused = [403, 404].includes(errorStatus(err) ?? 0);
      if (refused || ++failures >= MAX_CONSECUTIVE_FAILURES) {
        active = false;
        options.gone(refused);
        return;
      }
    }
    timer = setTimeout(poll, options.intervalMs);
  }

  void poll();
  return () => {
    active = false;
    clearTimeout(timer);
  };
}
