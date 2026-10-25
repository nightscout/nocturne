import { describe, it, expect, vi } from "vitest";
import type { Chat, SlashCommandEvent } from "chat";
import { registerAccountCommands } from "./account.js";
import { runWithContext, type BotRequestContext } from "../lib/request-context.js";
import type { BotApiClient } from "../types.js";

vi.mock("../lib/logger.js", () => ({
  createLogger: () => ({ info: vi.fn(), warn: vi.fn(), error: vi.fn(), debug: vi.fn() }),
}));

async function connect(platform: string, user: Record<string, unknown>, raw: unknown = {}, text = "sleepy") {
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
    text,
    user: { userId: "4242", ...user },
    raw,
  } as unknown as SlashCommandEvent;

  await runWithContext(context, () => commands.get("/connect")!(event));
  return { create, event };
}

async function connectedName(platform: string, user: Record<string, unknown>, raw: unknown = {}) {
  const { create } = await connect(platform, user, raw);
  expect(create).toHaveBeenCalledOnce();
  return create.mock.calls[0]?.[2];
}

describe("/connect", () => {
  it("records a Discord account's username", async () => {
    await expect(connectedName("discord", { userName: "sam_t", fullName: "Sam" })).resolves.toBe("sam_t");
  });

  it("records a Telegram account's handle from the platform's own user", async () => {
    await expect(
      connectedName("telegram", { userName: "sam_tester" }, { from: { id: 4242, username: "sam_tester" } }),
    ).resolves.toBe("sam_tester");
  });

  it("records no name for a Telegram account without a handle, whatever its first name says", async () => {
    await expect(
      connectedName("telegram", { userName: "alice_admin" }, { from: { id: 4242, first_name: "alice_admin" } }),
    ).resolves.toBeNull();
  });

  it("records no name for a WhatsApp account, whose profile name anyone can set", async () => {
    await expect(connectedName("whatsapp", { userName: "alice", fullName: "alice" })).resolves.toBeNull();
  });

  it("records no name for a Slack account, whose display name anyone can set", async () => {
    await expect(connectedName("slack", { userName: "alice", fullName: "Alice" })).resolves.toBeNull();
  });

  it("records no name that is not a well-formed handle", async () => {
    await expect(connectedName("discord", { userName: `alice${String.fromCharCode(0x202e)}gnp.exe` })).resolves.toBeNull();
  });

  it("accepts an instance slug of one DNS label, 63 characters", async () => {
    const { create } = await connect("discord", { userName: "sam_t" }, {}, "a".repeat(63));
    expect(create).toHaveBeenCalledOnce();
  });

  it("rejects an instance slug longer than one DNS label", async () => {
    const { create, event } = await connect("discord", { userName: "sam_t" }, {}, "a".repeat(64));
    expect(create).not.toHaveBeenCalled();
    expect(event.channel.postEphemeral).toHaveBeenCalledOnce();
  });
});
