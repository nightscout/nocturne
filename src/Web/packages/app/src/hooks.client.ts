import type { HandleClientError } from "@sveltejs/kit";

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
