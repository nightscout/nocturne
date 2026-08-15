/**
 * Runs `refresh` on an interval, but only while the tab is visible.
 *
 * A backgrounded tab shows nobody anything, so polling one bills a request a minute (or a request
 * every ten seconds) for a surface no one can see. Polling stops while the tab is hidden and
 * catches up immediately on return, so what the viewer sees on coming back is current.
 *
 * Call during component initialization: the timer and the listener are owned by an `$effect` and
 * are torn down with the component.
 */
export function pollWhileVisible(refresh: () => void, intervalMs: number): void {
  $effect(() => {
    let timer: ReturnType<typeof setInterval> | null = null;

    function stop() {
      if (timer) clearInterval(timer);
      timer = null;
    }

    function start() {
      if (timer) return;
      timer = setInterval(refresh, intervalMs);
    }

    function onVisibilityChange() {
      if (document.hidden) {
        stop();
      } else {
        // Whatever fired while hidden is unseen, so catch up immediately.
        refresh();
        start();
      }
    }

    if (!document.hidden) start();
    document.addEventListener("visibilitychange", onVisibilityChange);

    return () => {
      stop();
      document.removeEventListener("visibilitychange", onVisibilityChange);
    };
  });
}
