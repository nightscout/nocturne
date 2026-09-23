// A Tauri command rejects with the Rust `CommandError`, serialised as `{ status?, message }`.

/** The HTTP status the Rust side attached to a command's rejection, if any. */
export function commandErrorStatus(e: unknown): number | undefined {
  return typeof e === "object" && e !== null && "status" in e && typeof e.status === "number"
    ? e.status
    : undefined;
}

/** The message of a command's rejection, if it carries one. */
export function commandErrorMessage(e: unknown): string | undefined {
  return typeof e === "object" && e !== null && "message" in e && e.message != null
    ? String(e.message)
    : undefined;
}
