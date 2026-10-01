import { describe, it, expect, vi } from "vitest";
import { DiscordAdapter } from "@chat-adapter/discord";
import { SlackAdapter } from "@chat-adapter/slack";
import { createBot } from "./bot.js";
import { AccentedDiscordAdapter } from "./adapters/accented-discord.js";
import { AccentedSlackAdapter } from "./adapters/accented-slack.js";

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
