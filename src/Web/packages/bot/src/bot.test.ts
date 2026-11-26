import { afterEach, describe, it, expect, vi } from "vitest";
import type { TelegramAdapter } from "@chat-adapter/telegram";
import { DiscordAdapter } from "@chat-adapter/discord";
import { SlackAdapter } from "@chat-adapter/slack";
import { createBot } from "./bot.js";
import { AccentedDiscordAdapter } from "./adapters/accented-discord.js";
import { AccentedSlackAdapter } from "./adapters/accented-slack.js";
import type { NocturneWhatsAppAdapter } from "./adapters/nocturne-whatsapp.js";

/**
 * The Resend adapter resolves `react` from its own install, which this package
 * does not hoist; it is not part of what is under test here.
 */
vi.mock("@resend/chat-sdk-adapter", () => ({
  createResendAdapter: () => ({ name: "resend" }),
}));

vi.mock("./lib/logger.js", () => ({
  createLogger: () => ({
    info: vi.fn(),
    warn: vi.fn(),
    error: vi.fn(),
    debug: vi.fn(),
  }),
}));

const bot = () =>
  createBot({
    postgresUrl: "postgres://nobody@localhost:5432/not-connected",
    platforms: {
      discord: {
        enabled: true,
        botToken: "not-a-token",
        publicKey: "a".repeat(64),
        applicationId: "000000000000000000",
      },
      slack: { enabled: true, botToken: "not-a-token", signingSecret: "not-a-secret" },
      telegram: { enabled: true, botToken: "not-a-token" },
    },
  });

describe("createBot", () => {
  it("posts through the adapters that can colour an alert", () => {
    expect(bot().getAdapter("discord")).toBeInstanceOf(AccentedDiscordAdapter);
    expect(bot().getAdapter("slack")).toBeInstanceOf(AccentedSlackAdapter);
  });

  it("keeps the stock behaviour of every adapter it subclasses", () => {
    expect(bot().getAdapter("discord")).toBeInstanceOf(DiscordAdapter);
    expect(bot().getAdapter("slack")).toBeInstanceOf(SlackAdapter);
  });

  it("leaves the platforms with no colour to carry untouched", () => {
    const telegram = bot().getAdapter("telegram");

    expect(telegram).toBeDefined();
    expect(telegram).not.toBeInstanceOf(AccentedDiscordAdapter);
    expect(telegram).not.toBeInstanceOf(AccentedSlackAdapter);
    expect(telegram?.name).toBe("telegram");
  });
});

describe("the Telegram webhook", () => {
  afterEach(() => {
    vi.unstubAllEnvs();
  });

  const telegramWith = (telegram: boolean | { enabled: true; botToken: string; webhookSecretToken?: string }) =>
    createBot({
      postgresUrl: "postgres://nobody@localhost:5432/not-connected",
      platforms: { telegram },
    }).getAdapter("telegram") as unknown as TelegramAdapter;

  const update = (secretToken?: string) =>
    new Request("https://nocturne.test/api/v4/webhooks/telegram", {
      method: "POST",
      headers: secretToken ? { "X-Telegram-Bot-Api-Secret-Token": secretToken } : {},
      body: JSON.stringify({ update_id: 1 }),
    });

  it("accepts an update carrying the configured secret token", async () => {
    const telegram = telegramWith({ enabled: true, botToken: "not-a-token", webhookSecretToken: "the-secret" });

    expect((await telegram.handleWebhook(update("the-secret"))).status).toBe(200);
  });

  it("refuses an update without the configured secret token", async () => {
    const telegram = telegramWith({ enabled: true, botToken: "not-a-token", webhookSecretToken: "the-secret" });

    expect((await telegram.handleWebhook(update())).status).toBe(401);
    expect((await telegram.handleWebhook(update("a-guess"))).status).toBe(401);
  });

  it("refuses every update when neither the stored settings nor the environment carry a secret token", async () => {
    vi.stubEnv("TELEGRAM_WEBHOOK_SECRET_TOKEN", "");
    const telegram = telegramWith({ enabled: true, botToken: "not-a-token" });

    expect((await telegram.handleWebhook(update())).status).toBe(401);
  });

  it("falls back to the environment's secret token when the stored settings carry none", async () => {
    vi.stubEnv("TELEGRAM_WEBHOOK_SECRET_TOKEN", "env-secret");
    const telegram = telegramWith({ enabled: true, botToken: "not-a-token" });

    expect((await telegram.handleWebhook(update())).status).toBe(401);
    expect((await telegram.handleWebhook(update("env-secret"))).status).toBe(200);
  });

  it("prefers the stored secret token over the environment's", async () => {
    vi.stubEnv("TELEGRAM_WEBHOOK_SECRET_TOKEN", "env-secret");
    const telegram = telegramWith({ enabled: true, botToken: "not-a-token", webhookSecretToken: "the-secret" });

    expect((await telegram.handleWebhook(update("env-secret"))).status).toBe(401);
    expect((await telegram.handleWebhook(update("the-secret"))).status).toBe(200);
  });

  it("refuses every update when the environment carries no secret token", async () => {
    vi.stubEnv("TELEGRAM_BOT_TOKEN", "not-a-token");
    vi.stubEnv("TELEGRAM_WEBHOOK_SECRET_TOKEN", "");

    expect((await telegramWith(true).handleWebhook(update())).status).toBe(401);
  });

  it("checks the environment's secret token when configured from the environment", async () => {
    vi.stubEnv("TELEGRAM_BOT_TOKEN", "not-a-token");
    vi.stubEnv("TELEGRAM_WEBHOOK_SECRET_TOKEN", "env-secret");
    const telegram = telegramWith(true);

    expect((await telegram.handleWebhook(update())).status).toBe(401);
    expect((await telegram.handleWebhook(update("env-secret"))).status).toBe(200);
  });
});

describe("the WhatsApp webhook", () => {
  afterEach(() => {
    vi.unstubAllEnvs();
  });

  const whatsappWith = (whatsapp: boolean | { enabled: true; verifyToken: string }) =>
    createBot({
      postgresUrl: "postgres://nobody@localhost:5432/not-connected",
      platforms: {
        whatsapp:
          typeof whatsapp === "boolean"
            ? whatsapp
            : { ...whatsapp, accessToken: "not-a-token", appSecret: "not-a-secret", phoneNumberId: "1000" },
      },
    }).getAdapter("whatsapp")!;

  const verification = (verifyToken: string) =>
    new Request(
      `https://nocturne.test/api/v4/webhooks/whatsapp?hub.mode=subscribe&hub.verify_token=${verifyToken}&hub.challenge=1158201444`,
    );

  it("answers Meta's verification with the challenge when the verify token matches", async () => {
    const response = await whatsappWith({ enabled: true, verifyToken: "the-token" }).handleWebhook(
      verification("the-token"),
    );

    expect(response.status).toBe(200);
    expect(await response.text()).toBe("1158201444");
  });

  it("refuses Meta's verification when the verify token does not match", async () => {
    const response = await whatsappWith({ enabled: true, verifyToken: "the-token" }).handleWebhook(
      verification("a-guess"),
    );

    expect(response.status).toBe(403);
  });

  it("takes the alert template from the WhatsApp settings alone", () => {
    const adapterWith = (settings: Record<string, string>) =>
      createBot({
        postgresUrl: "postgres://nobody@localhost:5432/not-connected",
        platforms: {
          whatsapp: {
            enabled: true,
            accessToken: "not-a-token",
            appSecret: "not-a-secret",
            phoneNumberId: "1000",
            verifyToken: "not-a-token",
            ...settings,
          },
        },
      }).getAdapter("whatsapp") as NocturneWhatsAppAdapter;

    expect(adapterWith({}).alertTemplate).toBeNull();
    expect(adapterWith({ alertTemplateName: "nocturne_alert", alertTemplateLanguage: "en_US" }).alertTemplate).toEqual({
      name: "nocturne_alert",
      language: "en_US",
    });

    expect(adapterWith({ alertTemplateName: "nocturne_alert" }).alertTemplate).toEqual({
      name: "nocturne_alert",
      language: "en",
    });
  });

  it("checks the environment's verify token when configured from the environment", async () => {
    vi.stubEnv("WHATSAPP_ACCESS_TOKEN", "not-a-token");
    vi.stubEnv("WHATSAPP_APP_SECRET", "not-a-secret");
    vi.stubEnv("WHATSAPP_PHONE_NUMBER_ID", "1000");
    vi.stubEnv("WHATSAPP_VERIFY_TOKEN", "env-token");
    const whatsapp = whatsappWith(true);

    expect((await whatsapp.handleWebhook(verification("a-guess"))).status).toBe(403);
    expect((await whatsapp.handleWebhook(verification("env-token"))).status).toBe(200);
  });
});
