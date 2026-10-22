import type { SlashCommandEvent } from "chat";

/**
 * Formats of the platforms' unique, user-chosen handles. A link confirmation names the chat
 * account only by one of these: display names, first names and profile names are free text that
 * anyone can set to look like someone else, so platforms without a handle here are named by none.
 */
const HANDLE_FORMATS: Record<string, RegExp> = {
  discord: /^[a-z0-9_.]{2,32}$/,
  telegram: /^[A-Za-z0-9_]{5,32}$/,
};

/** `candidate` when it is a well-formed unique handle on `platform`, else null. */
export function platformHandle(platform: string, candidate: unknown): string | null {
  const format = HANDLE_FORMATS[platform];
  return format && typeof candidate === "string" && format.test(candidate) ? candidate : null;
}

function field(value: unknown, key: string): unknown {
  return typeof value === "object" && value !== null ? Reflect.get(value, key) : undefined;
}

/**
 * The invoking account's unique handle. Telegram's adapter falls back to the first name in
 * `user.userName`, so its handle is read from the raw message instead.
 */
export function slashCommandHandle(event: SlashCommandEvent): string | null {
  const platform = event.adapter.name;
  if (platform === "telegram") {
    const message = field(event.raw, "message") ?? event.raw;
    return platformHandle(platform, field(field(message, "from"), "username"));
  }
  return platformHandle(platform, event.user.userName);
}
