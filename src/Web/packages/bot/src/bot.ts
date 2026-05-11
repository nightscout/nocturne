import { Chat } from "chat";
import { createDiscordAdapter } from "@chat-adapter/discord";
import { createSlackAdapter } from "@chat-adapter/slack";
import { createTelegramAdapter } from "@chat-adapter/telegram";
import { createWhatsAppAdapter } from "@chat-adapter/whatsapp";
import { createPostgresState } from "@chat-adapter/state-pg";
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
}

export interface BotOptions {
  platforms?: {
    discord?: PlatformCredentials | boolean;
    slack?: PlatformCredentials | boolean;
    telegram?: PlatformCredentials | boolean;
    whatsapp?: PlatformCredentials | boolean;
  };
  postgresUrl: string;
}

export function createBot(options: BotOptions): Chat {
  const adapters: Record<string, any> = {};
  const platforms = options.platforms ?? {};

  const discord = platforms.discord;
  if (discord) {
    logger.info("Enabling Discord adapter");
    if (typeof discord === "object") {
      adapters.discord = createDiscordAdapter({
        botToken: discord.botToken!,
        publicKey: discord.publicKey!,
        applicationId: discord.applicationId!,
      });
    } else {
      adapters.discord = createDiscordAdapter(); // env var fallback
    }
  }

  const slack = platforms.slack;
  if (slack) {
    logger.info("Enabling Slack adapter");
    if (typeof slack === "object") {
      adapters.slack = createSlackAdapter({
        botToken: slack.botToken!,
        signingSecret: slack.signingSecret!,
      });
    } else {
      adapters.slack = createSlackAdapter(); // env var fallback
    }
  }

  const telegram = platforms.telegram;
  if (telegram) {
    logger.info("Enabling Telegram adapter");
    if (typeof telegram === "object") {
      adapters.telegram = createTelegramAdapter({
        botToken: telegram.botToken!,
      });
    } else {
      adapters.telegram = createTelegramAdapter(); // env var fallback
    }
  }

  const whatsapp = platforms.whatsapp;
  if (whatsapp) {
    if (typeof whatsapp === "object") {
      logger.info("Enabling WhatsApp adapter");
      adapters.whatsapp = createWhatsAppAdapter({
        accessToken: whatsapp.accessToken!,
        appSecret: whatsapp.appSecret!,
        phoneNumberId: whatsapp.phoneNumberId!,
        verifyToken: whatsapp.verifyToken!,
        userName: "nocturne",
        logger,
      });
    } else {
      logger.warn(
        "WhatsApp requires explicit credentials configured via the admin UI — skipping env var fallback",
      );
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
