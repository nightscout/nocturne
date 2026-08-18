import { describe, it, expect, vi, beforeEach, type Mock } from "vitest";
import type { ActionEvent, Chat } from "chat";
import { registerAlertCommands } from "./alerts.js";
import { runWithContext, type BotRequestContext } from "../lib/request-context.js";
import type { BotApiClient, DirectoryCandidate } from "../types.js";

vi.mock("../lib/logger.js", () => ({
  createLogger: () => ({
    info: vi.fn(),
    warn: vi.fn(),
    error: vi.fn(),
    debug: vi.fn(),
  }),
}));

const HOME_TENANT = "11111111-1111-1111-1111-111111111111";
const WORK_TENANT = "22222222-2222-2222-2222-222222222222";

const HOME = candidate(HOME_TENANT, "home-clinic", "home");
const WORK = candidate(WORK_TENANT, "work-clinic", "work");

function candidate(
  tenantId: string,
  tenantSlug: string,
  label: string,
): DirectoryCandidate {
  return {
    id: `link-${label}`,
    tenantId,
    tenantSlug,
    nocturneUserId: `nocturne-user-${label}`,
    label,
    displayName: label.toUpperCase(),
  };
}

function createContext(candidates: DirectoryCandidate[] | null) {
  const resolve = vi.fn().mockResolvedValue(candidates);
  const ambientAcknowledge = vi.fn().mockResolvedValue(undefined);
  const acknowledgeBySlug = new Map<string, Mock>();

  const scopedApiFactory = vi.fn((tenantSlug: string) => {
    let acknowledge = acknowledgeBySlug.get(tenantSlug);
    if (!acknowledge) {
      acknowledge = vi.fn().mockResolvedValue(undefined);
      acknowledgeBySlug.set(tenantSlug, acknowledge);
    }
    return { alerts: { acknowledge } } as unknown as BotApiClient;
  });

  const context: BotRequestContext = {
    unscopedApi: {
      directory: { resolve },
      alerts: { acknowledge: ambientAcknowledge },
    } as unknown as BotApiClient,
    scopedApiFactory,
    resolvedTenantSlug: null,
    resolvedLink: null,
  };

  return { context, resolve, scopedApiFactory, ambientAcknowledge, acknowledgeBySlug };
}

function createActionEvent(value?: string) {
  const post = vi.fn().mockResolvedValue({ id: "platform-message-1" });
  const postEphemeral = vi.fn().mockResolvedValue(null);
  const event = {
    actionId: "ack_alert",
    adapter: { name: "discord" },
    messageId: "platform-message-0",
    thread: { post, postEphemeral },
    threadId: "thread-1",
    user: { userId: "discord-user-1", fullName: "Sam Tester" },
    value,
  } as unknown as ActionEvent;
  return { event, post, postEphemeral };
}

describe("ack_alert action", () => {
  let handler: (event: ActionEvent) => Promise<void>;

  beforeEach(() => {
    const actions = new Map<string, (event: ActionEvent) => Promise<void>>();
    const bot = {
      onAction: (id: string, fn: (event: ActionEvent) => Promise<void>) => {
        actions.set(id, fn);
      },
      onSlashCommand: vi.fn(),
    } as unknown as Chat;

    registerAlertCommands(bot);
    handler = actions.get("ack_alert")!;
  });

  it("acknowledges through the client scoped to the alert's tenant", async () => {
    const ctx = createContext([HOME, WORK]);
    const { event, post } = createActionEvent(WORK_TENANT);

    await runWithContext(ctx.context, () => handler(event));

    expect(ctx.resolve).toHaveBeenCalledExactlyOnceWith("discord", "discord-user-1");
    expect(ctx.scopedApiFactory).toHaveBeenCalledExactlyOnceWith("work-clinic");
    expect(ctx.acknowledgeBySlug.get("work-clinic")).toHaveBeenCalledExactlyOnceWith({
      acknowledgedBy: "Sam Tester",
    });
    expect(ctx.acknowledgeBySlug.has("home-clinic")).toBe(false);
    expect(ctx.ambientAcknowledge).not.toHaveBeenCalled();
    expect(post).toHaveBeenCalledExactlyOnceWith("All alerts acknowledged.");
  });

  it("falls back to the only link when the button carries no tenant", async () => {
    const ctx = createContext([HOME]);
    const { event, post } = createActionEvent();

    await runWithContext(ctx.context, () => handler(event));

    expect(ctx.scopedApiFactory).toHaveBeenCalledExactlyOnceWith("home-clinic");
    expect(ctx.acknowledgeBySlug.get("home-clinic")).toHaveBeenCalledExactlyOnceWith({
      acknowledgedBy: "Sam Tester",
    });
    expect(post).toHaveBeenCalledExactlyOnceWith("All alerts acknowledged.");
  });

  it("tells an unlinked user to connect and calls no api", async () => {
    const ctx = createContext([]);
    const { event, post, postEphemeral } = createActionEvent(WORK_TENANT);

    await runWithContext(ctx.context, () => handler(event));

    expect(postEphemeral).toHaveBeenCalledExactlyOnceWith(
      event.user,
      "Your Discord account isn't linked to a Nocturne account yet. Run `/connect` to get started.",
      { fallbackToDM: true },
    );
    expect(ctx.scopedApiFactory).not.toHaveBeenCalled();
    expect(ctx.ambientAcknowledge).not.toHaveBeenCalled();
    expect(post).not.toHaveBeenCalled();
  });

  it("treats a missing directory response as unlinked", async () => {
    const ctx = createContext(null);
    const { event, post, postEphemeral } = createActionEvent(WORK_TENANT);

    await runWithContext(ctx.context, () => handler(event));

    expect(postEphemeral).toHaveBeenCalledOnce();
    expect(ctx.scopedApiFactory).not.toHaveBeenCalled();
    expect(ctx.ambientAcknowledge).not.toHaveBeenCalled();
    expect(post).not.toHaveBeenCalled();
  });

  it("refuses a tenant the tapping user has no link to", async () => {
    const ctx = createContext([HOME]);
    const { event, post, postEphemeral } = createActionEvent(WORK_TENANT);

    await runWithContext(ctx.context, () => handler(event));

    expect(postEphemeral).toHaveBeenCalledExactlyOnceWith(
      event.user,
      "That belongs to a Nocturne account you aren't linked to. Your linked accounts: `home`.",
      { fallbackToDM: true },
    );
    expect(ctx.scopedApiFactory).not.toHaveBeenCalled();
    expect(post).not.toHaveBeenCalled();
  });

  it("asks a multi-link user to choose when the button carries no tenant", async () => {
    const ctx = createContext([HOME, WORK]);
    const { event, post, postEphemeral } = createActionEvent();

    await runWithContext(ctx.context, () => handler(event));

    expect(postEphemeral).toHaveBeenCalledExactlyOnceWith(
      event.user,
      "You have multiple linked Nocturne accounts: `home` (HOME), `work` (WORK). Set a default in Settings → Integrations → Discord, or use the matching slash command with a label.",
      { fallbackToDM: true },
    );
    expect(ctx.scopedApiFactory).not.toHaveBeenCalled();
    expect(post).not.toHaveBeenCalled();
  });

  it("reports a failure without retrying against another tenant", async () => {
    const ctx = createContext([HOME, WORK]);
    const { event, post } = createActionEvent(WORK_TENANT);
    ctx.scopedApiFactory.mockImplementation(
      () =>
        ({
          alerts: { acknowledge: vi.fn().mockRejectedValue(new Error("503")) },
        }) as unknown as BotApiClient,
    );

    await runWithContext(ctx.context, () => handler(event));

    expect(ctx.scopedApiFactory).toHaveBeenCalledExactlyOnceWith("work-clinic");
    expect(post).toHaveBeenCalledExactlyOnceWith(
      "Failed to acknowledge. Please try again.",
    );
  });
});
