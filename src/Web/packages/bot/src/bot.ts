import { randomBytes } from "node:crypto";
import { Chat } from "chat";
import type { Adapter } from "chat";
import { createTelegramAdapter } from "@chat-adapter/telegram";
import { createResendAdapter } from "@resend/chat-sdk-adapter";
import { createPostgresState } from "@chat-adapter/state-pg";
import { AccentedDiscordAdapter } from "./adapters/accented-discord.js";
import { AccentedSlackAdapter } from "./adapters/accented-slack.js";
import { NocturneWhatsAppAdapter } from "./adapters/nocturne-whatsapp.js";
import { createLogger } from "./lib/logger.js";

const logger = createLogger();

export interface PlatformCredentials {
  enabled: boolean;
  botToken?: string;
  publicKey?: string;
  applicationId?: string;
  signingSecret?: string;
  accessToken?: string;
  appSecret?: string;
  phoneNumberId?: string;
  verifyToken?: string;
  fromAddress?: string;
  fromName?: string;
  apiKey?: string;
  webhookSecret?: string;
  webhookSecretToken?: string;
}

export interface BotOptions {
  platforms?: {
    discord?: PlatformCredentials | boolean;
    slack?: PlatformCredentials | boolean;
    telegram?: PlatformCredentials | boolean;
    whatsapp?: PlatformCredentials | boolean;
    resend?: PlatformCredentials | boolean;
  };
  postgresUrl: string;
}

/**
 * The Telegram adapter skips verification when it has no secret token, which would leave its
 * anonymous webhook accepting any caller's updates. Without a configured token it gets a random
 * one nobody else holds, so webhook calls are refused while long polling still works.
 */
function telegramWebhookSecretToken(configured: string | undefined): string {
  if (configured) return configured;
  logger.warn(
    "No Telegram webhook secret token is configured; Telegram webhook calls will be refused until one is set.",
  );
  return randomBytes(32).toString("hex");
}

/** A WhatsApp setting from the stored platform settings, else the environment. Refuses to start without one. */
function whatsappSetting(name: string, stored: string | undefined, envVar: string): string {
  const value = stored || process.env[envVar];
  if (!value) {
    throw new Error(`WhatsApp ${name} is required. Set it in the WhatsApp settings or ${envVar}.`);
  }
  return value;
}

export function createBot(options: BotOptions): Chat {
  const adapters: Record<string, Adapter> = {};
  const platforms = options.platforms ?? {};

  const discord = platforms.discord;
  if (discord) {
    logger.info("Enabling Discord adapter");
    if (typeof discord === "object") {
      adapters.discord = new AccentedDiscordAdapter({
        botToken: discord.botToken!,
        publicKey: discord.publicKey!,
        applicationId: discord.applicationId!,
      });
    } else {
      adapters.discord = new AccentedDiscordAdapter(); // env var fallback
    }
  }

  const slack = platforms.slack;
  if (slack) {
    logger.info("Enabling Slack adapter");
    if (typeof slack === "object") {
      adapters.slack = new AccentedSlackAdapter({
        botToken: slack.botToken!,
        signingSecret: slack.signingSecret!,
      });
    } else {
      adapters.slack = new AccentedSlackAdapter(); // env var fallback
    }
  }

  const telegram = platforms.telegram;
  if (telegram) {
    logger.info("Enabling Telegram adapter");
    const configured = typeof telegram === "object" ? telegram : undefined;
    adapters.telegram = createTelegramAdapter({
      botToken: configured?.botToken,
      secretToken: telegramWebhookSecretToken(
        configured?.webhookSecretToken || process.env.TELEGRAM_WEBHOOK_SECRET_TOKEN,
      ),
    });
  }

  const whatsapp = platforms.whatsapp;
  if (whatsapp) {
    logger.info("Enabling WhatsApp adapter");
    const configured = typeof whatsapp === "object" ? whatsapp : undefined;
    adapters.whatsapp = new NocturneWhatsAppAdapter({
      accessToken: whatsappSetting("accessToken", configured?.accessToken, "WHATSAPP_ACCESS_TOKEN"),
      appSecret: whatsappSetting("appSecret", configured?.appSecret, "WHATSAPP_APP_SECRET"),
      phoneNumberId: whatsappSetting("phoneNumberId", configured?.phoneNumberId, "WHATSAPP_PHONE_NUMBER_ID"),
      verifyToken: whatsappSetting("verifyToken", configured?.verifyToken, "WHATSAPP_VERIFY_TOKEN"),
      apiUrl: process.env.WHATSAPP_API_URL,
      userName: "nocturne",
      logger,
    });
  }

  const resend = platforms.resend;
  if (resend) {
    if (typeof resend === "object") {
      logger.info("Enabling Resend adapter");
      adapters.resend = createResendAdapter({
        fromAddress: resend.fromAddress!,
        fromName: resend.fromName,
        apiKey: resend.apiKey!,
        webhookSecret: resend.webhookSecret,
      });
    } else {
      adapters.resend = createResendAdapter({
        fromAddress: process.env.RESEND_FROM_ADDRESS!,
        fromName: process.env.RESEND_FROM_NAME,
      }); // apiKey + webhookSecret auto-load from env
    }
  }

  if (Object.keys(adapters).length === 0) {
    logger.warn("No platform adapters configured.");
  }

  return new Chat({
    userName: "nocturne",
    adapters,
    state: createPostgresState({ url: options.postgresUrl }),
  });
}
