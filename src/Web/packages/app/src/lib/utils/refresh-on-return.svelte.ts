import { afterNavigate } from "$app/navigation";

/**
 * Runs `refresh` when the tab becomes visible again and, given `intervalMs`, on that interval
 * while the tab is visible. The interval is stopped while the tab is hidden and restarted on
 * return, after the refresh that return triggers.
 *
 * Call during component initialization.
 */
export function refreshOnVisible(refresh: () => void, intervalMs?: number): void {
  $effect(() => {
    let timer: ReturnType<typeof setInterval> | undefined;

    function start() {
      if (intervalMs !== undefined && timer === undefined) {
        timer = setInterval(refresh, intervalMs);
      }
    }

    function stop() {
      clearInterval(timer);
      timer = undefined;
    }

    function onVisibilityChange() {
      if (document.hidden) {
        stop();
      } else {
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

/**
 * Runs `refresh` after each client navigation and when the tab becomes visible again, so a
 * long-lived surface showing a point-in-time value is re-read when someone looks at it.
 * The first navigation callback after mount is skipped: the caller's own bootstrap covers it.
 *
 * Call during component initialization.
 */
export function refreshOnReturn(refresh: () => void): void {
  let mounted = false;
  afterNavigate(() => {
    if (mounted) refresh();
    mounted = true;
  });

  refreshOnVisible(refresh);
}
