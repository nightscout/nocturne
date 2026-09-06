/**
 * A wall clock for text that ages on screen ("3m ago", "today").
 *
 * `timeAgo(t)` and `new Date()` read the clock once, so a value derived from
 * them freezes at first render. Reading `now.current` instead re-derives on
 * every tick.
 *
 * Instantiate this per component during initialisation. A module-level instance
 * would share both its interval and its value across concurrent SSR requests.
 */
export class Now {
  #current = $state(Date.now());

  /**
   * @param intervalMs How often to tick. Browsers throttle timers in hidden
   *   tabs, so a value under a second buys nothing there.
   */
  constructor(intervalMs = 30_000) {
    $effect(() => {
      const id = setInterval(() => (this.#current = Date.now()), intervalMs);
      return () => clearInterval(id);
    });
  }

  /** Unix milliseconds, as of the last tick. */
  get current(): number {
    return this.#current;
  }

  /** The current local date as `YYYY-MM-DD`, for date inputs and day filters. */
  get localDate(): string {
    const d = new Date(this.#current);
    const month = `${d.getMonth() + 1}`.padStart(2, "0");
    const day = `${d.getDate()}`.padStart(2, "0");
    return `${d.getFullYear()}-${month}-${day}`;
  }
}
