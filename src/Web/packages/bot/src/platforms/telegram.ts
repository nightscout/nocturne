import { randomBytes } from "node:crypto";
import type { SlashCommandEvent } from "chat";
import { createTelegramAdapter } from "@chat-adapter/telegram";
import { createLogger } from "../lib/logger.js";
import type { PlatformModule } from "./platform.js";

const logger = createLogger();

/**
 * The Telegram adapter skips verification when it has no secret token, which would leave its
 * anonymous webhook accepting any caller's updates. Without a configured token it gets a random
 * one nobody else holds, so every webhook call is refused until a token is configured.
 */
function webhookSecretToken(configured: string | undefined): string {
  if (configured) return configured;
  logger.warn(
    "No Telegram webhook secret token is configured; Telegram webhook calls will be refused until one is set.",
  );
  return randomBytes(32).toString("hex");
}

function field(value: unknown, key: string): unknown {
  return typeof value === "object" && value !== null ? Reflect.get(value, key) : undefined;
}

export const telegram = {
  name: "telegram",
  displayName: "Telegram",
  commands: "slash",
  webhookMethods: ["POST"],
  channelTypes: { telegram_dm: "direct", telegram_group: "channel" },
  handleFormat: /^[A-Za-z0-9_]{5,32}$/,
  /** The adapter falls back to the first name in `user.userName`, so the handle is read from the raw message. */
  slashCommandHandle: (event: SlashCommandEvent) => {
    const message = field(event.raw, "message") ?? event.raw;
    return field(field(message, "from"), "username");
  },
  fromSettings: (fields) => ({
    enabled: true,
    botToken: fields["botToken"],
    webhookSecretToken: fields["webhookSecretToken"],
  }),
  configuredInEnvironment: (env) => !!env.TELEGRAM_BOT_TOKEN,
  createAdapter: (credentials) =>
    createTelegramAdapter({
      botToken: credentials?.botToken,
      // The default mode starts long polling when no webhook is registered, which runs handlers outside
      // any request, where they have no API client. Updates arrive only through the registered webhook.
      mode: "webhook",
      // Kept history holds people's messages in the bot's state; see purgeThreadHistory.
      persistThreadHistory: false,
      secretToken: webhookSecretToken(credentials?.webhookSecretToken || process.env.TELEGRAM_WEBHOOK_SECRET_TOKEN),
    }),
} as const satisfies PlatformModule;
