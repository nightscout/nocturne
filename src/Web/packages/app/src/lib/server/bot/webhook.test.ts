import { beforeEach, describe, expect, it, vi } from "vitest";
import type { Cookies } from "@sveltejs/kit";
import { getUnscopedApi, getScopedApiFactory, type BotApiClient } from "@nocturne/bot";

const unscoped = { name: "unscoped" } as unknown as BotApiClient;
const scoped = { name: "scoped" } as unknown as BotApiClient;
const webhooks: Record<string, (request: Request) => Promise<Response>> = {};

// The bot's entry point loads every adapter; the request context is the part under test, so it
// comes from the bot's own source, beside the webhook methods of two platforms.
vi.mock("@nocturne/bot", async () => {
  const { createRequire } = await import("node:module");
  const { dirname, join } = await import("node:path");
  const { pathToFileURL } = await import("node:url");
  const root = dirname(createRequire(import.meta.url).resolve("@nocturne/bot/package.json"));
  const context = await import(pathToFileURL(join(root, "src/lib/request-context.ts")).href);
  const methods: Record<string, string[]> = { discord: ["POST"], whatsapp: ["GET", "POST"] };
  return {
    ...context,
    platformModule: (name: string) => (methods[name] ? { name, webhookMethods: methods[name] } : undefined),
  };
});
vi.mock("./index.js", () => ({ getBot: async () => ({ webhooks }) }));
vi.mock("./api-client.js", () => ({
  buildUnscopedBotApiClient: vi.fn(() => unscoped),
  buildScopedBotApiClient: vi.fn(() => scoped),
}));
vi.mock("#lib/server/request-host.js", () => ({
  getEffectiveHost: () => "nocturne.test",
  getOriginalProto: () => "https",
}));

const { handleBotWebhook } = await import("./webhook");
const { buildScopedBotApiClient } = await import("./api-client.js");

const event = (request: Request) => ({ request, cookies: {} as Cookies, fetch });

describe("handleBotWebhook", () => {
  beforeEach(() => {
    for (const name of Object.keys(webhooks)) delete webhooks[name];
  });

  it("runs the adapter inside the bot's request context", async () => {
    let seen: { unscoped: BotApiClient; scoped: BotApiClient } | undefined;
    webhooks.whatsapp = async () => {
      seen = { unscoped: getUnscopedApi(), scoped: getScopedApiFactory()("family") };
      return new Response("ok");
    };

    await handleBotWebhook("whatsapp", event(new Request("https://nocturne.test/api/v4/webhooks/whatsapp", { method: "POST" })));

    expect(seen).toEqual({ unscoped, scoped });
    expect(buildScopedBotApiClient).toHaveBeenCalledWith(fetch, "family");
  });

  it("hands a GET to the adapter unchanged, so Meta's verification reaches it", async () => {
    const request = new Request("https://nocturne.test/api/v4/webhooks/whatsapp?hub.mode=subscribe&hub.challenge=42");
    const handled = vi.fn(async () => new Response("42"));
    webhooks.whatsapp = handled;

    const response = await handleBotWebhook("whatsapp", event(request));

    expect(handled).toHaveBeenCalledWith(request);
    expect(await response.text()).toBe("42");
  });

  it("answers 404 for a platform the bot does not know", async () => {
    webhooks.matrix = vi.fn(async () => new Response("ok"));

    const response = await handleBotWebhook(
      "matrix",
      event(new Request("https://nocturne.test/api/v4/webhooks/matrix", { method: "POST" })),
    );

    expect(response.status).toBe(404);
    expect(webhooks.matrix).not.toHaveBeenCalled();
  });

  it("answers 405 for a method the platform does not call its webhook with", async () => {
    webhooks.discord = vi.fn(async () => new Response("ok"));

    const response = await handleBotWebhook("discord", event(new Request("https://nocturne.test/api/v4/webhooks/discord")));

    expect(response.status).toBe(405);
    expect(response.headers.get("Allow")).toBe("POST");
    expect(webhooks.discord).not.toHaveBeenCalled();
  });

  it("answers 404 for a platform whose bot is not enabled", async () => {
    const response = await handleBotWebhook(
      "whatsapp",
      event(new Request("https://nocturne.test/api/v4/webhooks/whatsapp", { method: "POST" })),
    );

    expect(response.status).toBe(404);
  });
});
