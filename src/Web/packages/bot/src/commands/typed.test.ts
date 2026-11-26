import { createHmac } from "node:crypto";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Chat } from "chat";
import { createMemoryState } from "@chat-adapter/state-memory";
import { createBot } from "../bot.js";
import { runWithContext, type BotRequestContext } from "../lib/request-context.js";
import type { BotApiClient } from "../types.js";
import { DISCORD_COMMAND_MANIFEST } from "./manifest.js";
import { registerAllCommands } from "./index.js";
import { parseTypedCommand, registerTypedCommands, typedCommandHelp } from "./typed.js";

vi.mock("../lib/logger.js", () => ({
  createLogger: () => ({ info: vi.fn(), warn: vi.fn(), error: vi.fn(), debug: vi.fn(), child: vi.fn() }),
}));

/** The bot as it runs, on an in-memory state in place of Postgres. */
vi.mock("@chat-adapter/state-pg", async () => {
  const { createMemoryState } = await import("@chat-adapter/state-memory");
  return { createPostgresState: () => createMemoryState() };
});

/** Resolves `react` from its own install, which this package does not hoist; not under test here. */
vi.mock("@resend/chat-sdk-adapter", () => ({ createResendAdapter: () => ({ name: "resend" }) }));

describe("parseTypedCommand", () => {
  it.each([
    ["/connect myfamily", { command: "/connect", text: "myfamily" }],
    ["  /BG  ", { command: "/bg", text: "" }],
    ["/bg  home \n", { command: "/bg", text: "home" }],
    ["/alerts\nwork", { command: "/alerts", text: "work" }],
  ])("reads %j", (text, expected) => {
    expect(parseTypedCommand(text)).toEqual(expected);
  });

  it.each(["hello", "", "/", "/ bg", "what is /bg?"])("finds no command in %j", (text) => {
    expect(parseTypedCommand(text)).toBeNull();
  });
});

describe("typedCommandHelp", () => {
  it("lists every command with its argument", () => {
    const help = typedCommandHelp(null);

    for (const command of DISCORD_COMMAND_MANIFEST) expect(help).toContain(`/${command.name}`);
    expect(help).toContain("/connect [slug]: ");
  });

  it("names a command it does not know", () => {
    expect(typedCommandHelp("/help")).toMatch(/^I do not know the command \/help\./);
  });
});

describe("registerTypedCommands", () => {
  async function register() {
    let onDirectMessage: ((...args: unknown[]) => Promise<void>) | undefined;
    const processSlashCommand = vi.fn();
    const state = createMemoryState();
    await state.connect();
    const bot = {
      onDirectMessage: (handler: (...args: unknown[]) => Promise<void>) => {
        onDirectMessage = handler;
      },
      processSlashCommand,
      getState: () => state,
    } as unknown as Chat;
    registerTypedCommands(bot);
    const post = vi.fn();
    const dm = (platform: string, text: string) =>
      onDirectMessage!(
        { id: `${platform}:thread`, adapter: { name: platform }, channelId: `${platform}:channel`, post },
        { text, author: { userId: "user-1", isMe: false }, raw: { text } },
      );
    return { dm, post, processSlashCommand };
  }

  it("runs a known command as the slash command, with its argument as the text", async () => {
    const { dm, processSlashCommand } = await register();

    await dm("whatsapp", "/connect myfamily");

    expect(processSlashCommand).toHaveBeenCalledExactlyOnceWith(
      {
        adapter: { name: "whatsapp" },
        channelId: "whatsapp:channel",
        command: "/connect",
        text: "myfamily",
        user: { userId: "user-1", isMe: false },
        raw: { text: "/connect myfamily" },
      },
      undefined,
    );
  });

  it.each(["hello", "/foo"])("answers %j with the commands", async (text) => {
    const { dm, post, processSlashCommand } = await register();

    await dm("whatsapp", text);

    expect(processSlashCommand).not.toHaveBeenCalled();
    expect(post).toHaveBeenCalledExactlyOnceWith(expect.stringContaining("/connect"));
  });

  it("answers other messages with the commands once in ten minutes, so it cannot loop with another auto-responder", async () => {
    const { dm, post } = await register();

    await dm("whatsapp", "hello");
    await dm("whatsapp", "Thank you for your message.");

    expect(post).toHaveBeenCalledOnce();
  });

  it("names a command it does not know every time, even just after the list", async () => {
    const { dm, post } = await register();

    await dm("whatsapp", "hello");
    await dm("whatsapp", "/bgg");
    await dm("whatsapp", "/bgg");

    expect(post).toHaveBeenCalledTimes(3);
    expect(post).toHaveBeenLastCalledWith(expect.stringMatching(/^I do not know the command \/bgg\./));
  });

  it.each(["/help", "/start", "/HELP"])("answers %j with the commands every time", async (text) => {
    const { dm, post } = await register();

    await dm("whatsapp", text);
    await dm("whatsapp", text);

    expect(post).toHaveBeenCalledTimes(2);
    expect(post).toHaveBeenCalledWith(expect.stringMatching(/^Send one of these commands:/));
  });

  it.each(["discord", "slack", "telegram"])(
    "leaves direct messages on %s, which has slash commands, alone",
    async (platform) => {
      const { dm, post, processSlashCommand } = await register();

      await dm(platform, "/bg");
      await dm(platform, "hello");

      expect(processSlashCommand).not.toHaveBeenCalled();
      expect(post).not.toHaveBeenCalled();
    },
  );

  it("knows exactly the commands that have a handler", () => {
    const registered: string[] = [];
    const bot = {
      onSlashCommand: (name: string) => registered.push(name),
      onAction: vi.fn(),
      onDirectMessage: vi.fn(),
    } as unknown as Chat;

    registerAllCommands(bot, "nocturne.test");

    expect(registered.sort()).toEqual(DISCORD_COMMAND_MANIFEST.map((c) => `/${c.name}`).sort());
  });
});

describe("a command typed on WhatsApp", () => {
  const APP_SECRET = "the-app-secret";
  const PHONE_NUMBER_ID = "1000";
  const WA_ID = "61400000000";

  let sent: Array<Record<string, unknown>>;

  beforeEach(() => {
    sent = [];
    vi.stubGlobal(
      "fetch",
      vi.fn(async (_url: string, init?: RequestInit) => {
        if (init?.body) sent.push(JSON.parse(String(init.body)));
        return new Response(JSON.stringify({ messages: [{ id: "wamid.out" }], success: true }));
      }),
    );
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  async function typed(texts: string[], api: Record<string, unknown>, scopedApi: Record<string, unknown> = {}) {
    const chat = createBot({
      postgresUrl: "postgres://nobody@localhost:5432/not-connected",
      platforms: {
        whatsapp: {
          enabled: true,
          accessToken: "not-a-token",
          appSecret: APP_SECRET,
          phoneNumberId: PHONE_NUMBER_ID,
          verifyToken: "not-a-token",
        },
      },
    });
    registerAllCommands(chat, "nocturne.test");

    const body = JSON.stringify({
      object: "whatsapp_business_account",
      entry: [
        {
          id: "waba",
          changes: [
            {
              field: "messages",
              value: {
                messaging_product: "whatsapp",
                metadata: { phone_number_id: PHONE_NUMBER_ID },
                contacts: [{ wa_id: WA_ID, profile: { name: "Alex" } }],
                messages: texts.map((text, i) => ({
                  from: WA_ID,
                  id: `wamid.in.${i}`,
                  timestamp: String(Math.floor(Date.now() / 1000)),
                  type: "text",
                  text: { body: text },
                })),
              },
            },
          ],
        },
      ],
    });
    const signature = createHmac("sha256", APP_SECRET).update(body).digest("hex");
    const request = new Request("https://nocturne.test/api/v4/webhooks/whatsapp", {
      method: "POST",
      headers: { "x-hub-signature-256": `sha256=${signature}` },
      body,
    });
    const context: BotRequestContext = {
      unscopedApi: api as unknown as BotApiClient,
      scopedApiFactory: vi.fn(() => scopedApi as unknown as BotApiClient),
      resolvedTenantSlug: null,
      resolvedLink: null,
    };
    const tasks: Promise<unknown>[] = [];
    await runWithContext(context, async () => {
      const response = await chat.webhooks.whatsapp!(request, { waitUntil: (task) => tasks.push(task) });
      expect(response.status).toBe(200);
      await Promise.all(tasks);
    });
  }

  const textsSent = () => sent.map((m) => String((m.text as { body?: string } | undefined)?.body ?? ""));

  it("links the sender's WhatsApp account with /connect, sending the link to them alone", async () => {
    const create = vi.fn().mockResolvedValue({ token: "pending-token" });

    await typed(["/connect sleepy"], { pendingLinks: { create } });

    expect(create).toHaveBeenCalledExactlyOnceWith("whatsapp", WA_ID, null, "sleepy", "connect-slash");
    expect(sent).toHaveLength(1);
    expect(sent[0]).toMatchObject({ to: `+${WA_ID}` });
    expect(textsSent()[0]).toContain("https://sleepy.nocturne.test/auth/bot/authorize?state=pending-token");
  });

  it("tells an unlinked sender to connect, naming WhatsApp", async () => {
    const resolve = vi.fn().mockResolvedValue([]);

    await typed(["/status"], { directory: { resolve } });

    expect(resolve).toHaveBeenCalledExactlyOnceWith("whatsapp", WA_ID);
    expect(textsSent()).toEqual([
      "Your WhatsApp account isn't linked to a Nocturne account yet. Run `/connect` to get started.",
    ]);
  });

  it("runs a command for a linked sender with the API scoped to their Nocturne account", async () => {
    const resolve = vi.fn().mockResolvedValue([
      {
        id: "link-1",
        tenantId: "11111111-1111-1111-1111-111111111111",
        tenantSlug: "sleepy",
        nocturneUserId: "user-1",
        label: "home",
        displayName: "Home",
        isDefault: true,
      },
    ]);
    const getAll = vi.fn().mockResolvedValue({ data: [] });

    await typed(["/BG"], { directory: { resolve } }, { sensorGlucose: { getAll } });

    expect(getAll).toHaveBeenCalledOnce();
    expect(textsSent()).toEqual(["No recent glucose readings found for Home."]);
  });

  it("runs every command of a batch Meta delivers at once", async () => {
    const resolve = vi.fn().mockResolvedValue([]);

    await typed(["/status", "/alerts"], { directory: { resolve } });

    expect(resolve).toHaveBeenCalledTimes(2);
    expect(textsSent()).toHaveLength(2);
  });

  it("answers a message that is not a command with the commands", async () => {
    await typed(["hello"], {});

    expect(textsSent()).toHaveLength(1);
    expect(textsSent()[0]).toContain("Send one of these commands:");
  });
});
