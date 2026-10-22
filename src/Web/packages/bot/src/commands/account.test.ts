import { describe, it, expect, vi } from "vitest";
import type { Chat, SlashCommandEvent } from "chat";
import { registerAccountCommands } from "./account.js";
import { runWithContext, type BotRequestContext } from "../lib/request-context.js";
import type { BotApiClient } from "../types.js";

vi.mock("../lib/logger.js", () => ({
  createLogger: () => ({ info: vi.fn(), warn: vi.fn(), error: vi.fn(), debug: vi.fn() }),
}));

async function connect(platform: string, user: Record<string, unknown>, raw: unknown = {}) {
  const commands = new Map<string, (event: SlashCommandEvent) => Promise<void>>();
  const bot = {
    onSlashCommand: (name: string, fn: (event: SlashCommandEvent) => Promise<void>) => {
      commands.set(name, fn);
    },
  } as unknown as Chat;
  registerAccountCommands(bot, "nocturne.test");

  const create = vi.fn().mockResolvedValue({ token: "pending-token" });
  const context: BotRequestContext = {
    unscopedApi: { pendingLinks: { create } } as unknown as BotApiClient,
    scopedApiFactory: vi.fn(),
    resolvedTenantSlug: null,
    resolvedLink: null,
  };
  const event = {
    adapter: { name: platform },
    channel: { post: vi.fn(), postEphemeral: vi.fn().mockResolvedValue(null) },
    text: "sleepy",
    user: { userId: "4242", ...user },
    raw,
  } as unknown as SlashCommandEvent;

  await runWithContext(context, () => commands.get("/connect")!(event));
  expect(create).toHaveBeenCalledOnce();
  return create.mock.calls[0]?.[2];
}

describe("/connect", () => {
  it("records a Discord account's username", async () => {
    await expect(connect("discord", { userName: "sam_t", fullName: "Sam" })).resolves.toBe("sam_t");
  });

  it("records a Telegram account's handle from the platform's own user", async () => {
    await expect(
      connect("telegram", { userName: "sam_tester" }, { from: { id: 4242, username: "sam_tester" } }),
    ).resolves.toBe("sam_tester");
  });

  it("records no name for a Telegram account without a handle, whatever its first name says", async () => {
    await expect(
      connect("telegram", { userName: "alice_admin" }, { from: { id: 4242, first_name: "alice_admin" } }),
    ).resolves.toBeNull();
  });

  it("records no name for a WhatsApp account, whose profile name anyone can set", async () => {
    await expect(connect("whatsapp", { userName: "alice", fullName: "alice" })).resolves.toBeNull();
  });

  it("records no name for a Slack account, whose display name anyone can set", async () => {
    await expect(connect("slack", { userName: "alice", fullName: "Alice" })).resolves.toBeNull();
  });

  it("records no name that is not a well-formed handle", async () => {
    await expect(connect("discord", { userName: `alice${String.fromCharCode(0x202e)}gnp.exe` })).resolves.toBeNull();
  });
});
