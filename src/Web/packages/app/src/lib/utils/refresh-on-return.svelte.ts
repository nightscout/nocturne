import { afterNavigate } from "$app/navigation";

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

  $effect(() => {
    function onVisibilityChange() {
      if (!document.hidden) refresh();
    }

    document.addEventListener("visibilitychange", onVisibilityChange);
    return () => document.removeEventListener("visibilitychange", onVisibilityChange);
  });
}
