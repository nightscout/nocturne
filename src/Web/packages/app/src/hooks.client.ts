import type { ClientInit, HandleClientError } from "@sveltejs/kit";

const PRELOAD_RELOAD_KEY = "nocturne:preload-error-reload";
const PRELOAD_RELOAD_WINDOW_MS = 10_000;

/**
 * A chunk that fails to load after a deploy usually belongs to the build this tab started on, so
 * one reload fetches the current one. A second failure within the window is left to surface, so a
 * chunk that is genuinely missing cannot reload the tab in a loop.
 */
export const init: ClientInit = () => {
  window.addEventListener("vite:preloadError", () => {
    try {
      const last = Number(sessionStorage.getItem(PRELOAD_RELOAD_KEY));
      if (Date.now() - last < PRELOAD_RELOAD_WINDOW_MS) return;
      sessionStorage.setItem(PRELOAD_RELOAD_KEY, String(Date.now()));
    } catch {
      return;
    }
    window.location.reload();
  });
};

export const handleError: HandleClientError = ({ error }) => {
  const errorId = crypto.randomUUID();

  const message =
    error instanceof Error
      ? error.message
      : typeof error === "string"
        ? error
        : "An unexpected error occurred";

  const stack = error instanceof Error ? error.stack : undefined;

  console.error(`Error ID: ${errorId}`, error);

  // Fire-and-forget — do not await, do not retry
  fetch("/api/otel/errors", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      message,
      stack,
      url: window.location.href,
      errorId,
    }),
  }).catch(() => {
    // Swallow — reporting failure should never mask the original error
  });

  return { message, errorId };
};
