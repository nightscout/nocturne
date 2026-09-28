import { describe, it, expect, vi, beforeEach, type Mock } from "vitest";
import type { ActionEvent, Chat, SlashCommandEvent } from "chat";
import { registerAlertCommands } from "./alerts.js";
import { runWithContext, type BotRequestContext } from "../lib/request-context.js";
import type {
  ActiveExcursion,
  BotApiClient,
  DirectoryCandidate,
} from "../types.js";
import { encodeActionValue, encodeTenantKey } from "../lib/action-value.js";
import { cardFields, cardTexts, cardTitle } from "../cards/card.test-utils.js";

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
const EXCURSION = "33333333-3333-3333-3333-333333333333";

const HOME = candidate(HOME_TENANT, "home-clinic", "home");
const WORK = candidate(WORK_TENANT, "work-clinic", "work");

/** Two tenant ids whose trailing bytes — and so whose button-value keys — are identical. */
const TWIN_TENANT_A = "11111111-1111-7000-8000-000000000001";
const TWIN_TENANT_B = "22222222-2222-7000-8000-000000000001";
const TWIN_A = candidate(TWIN_TENANT_A, "twin-a-clinic", "twin-a");
const TWIN_B = candidate(TWIN_TENANT_B, "twin-b-clinic", "twin-b");

/** The value the alert card puts on its buttons: the tenant and the excursion. */
const cardValue = (tenantId: string, excursionId: string) =>
  encodeActionValue({ tenantId, excursionId });

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
    isDefault: false,
  };
}

const asDefault = (c: DirectoryCandidate): DirectoryCandidate => ({
  ...c,
  isDefault: true,
});

interface ScopedAlerts {
  acknowledgeAsLinkedMember: Mock;
  getActiveAlerts: Mock;
}

function createContext(
  candidates: DirectoryCandidate[] | null,
  activeAlerts: ActiveExcursion[] | null = [],
) {
  const resolve = vi.fn().mockResolvedValue(candidates);
  const ambientAcknowledge = vi.fn().mockResolvedValue(undefined);
  const alertsBySlug = new Map<string, ScopedAlerts>();

  const scopedApiFactory = vi.fn((tenantSlug: string) => {
    let alerts = alertsBySlug.get(tenantSlug);
    if (!alerts) {
      alerts = {
        acknowledgeAsLinkedMember: vi.fn().mockResolvedValue({
          outcome: "acknowledged",
          acknowledgedBy: "Sam Tester",
          alreadyAcknowledged: false,
        }),
        getActiveAlerts: vi.fn().mockResolvedValue(activeAlerts),
      };
      alertsBySlug.set(tenantSlug, alerts);
    }
    return { alerts } as unknown as BotApiClient;
  });

  const context: BotRequestContext = {
    unscopedApi: {
      directory: { resolve },
      alerts: { acknowledgeAsLinkedMember: ambientAcknowledge },
    } as unknown as BotApiClient,
    scopedApiFactory,
    resolvedTenantSlug: null,
    resolvedLink: null,
  };

  return { context, resolve, scopedApiFactory, ambientAcknowledge, alertsBySlug };
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

function createSlashEvent() {
  const post = vi.fn().mockResolvedValue({ id: "platform-message-1" });
  const postEphemeral = vi.fn().mockResolvedValue(null);
  const event = {
    adapter: { name: "discord" },
    channel: { post, postEphemeral },
    command: "/alerts",
    text: "",
    user: { userId: "discord-user-1", fullName: "Sam Tester" },
  } as unknown as SlashCommandEvent;
  return { event, post, postEphemeral };
}

function registerHandlers() {
  const actions = new Map<string, (event: ActionEvent) => Promise<void>>();
  const commands = new Map<
    string,
    (event: SlashCommandEvent) => Promise<void>
  >();
  const bot = {
    onAction: (id: string, fn: (event: ActionEvent) => Promise<void>) => {
      actions.set(id, fn);
    },
    onSlashCommand: (
      name: string,
      fn: (event: SlashCommandEvent) => Promise<void>,
    ) => {
      commands.set(name, fn);
    },
  } as unknown as Chat;

  registerAlertCommands(bot);
  return { actions, commands };
}

/** The confirmation the handler posts is a card, so assert on its text. */
const postedText = (post: Mock, call = 0) =>
  cardTexts(post.mock.calls[call]?.[0]);

/** The request that acknowledges as the tapping user's linked member. */
const asLinkedMember = (excursionId: string | null) => ({
  platform: "discord",
  platformUserId: "discord-user-1",
  excursionId,
  acknowledgedBy: "Sam Tester",
});

describe("ack_alert action", () => {
  let handler: (event: ActionEvent) => Promise<void>;

  beforeEach(() => {
    handler = registerHandlers().actions.get("ack_alert")!;
  });

  it("acknowledges only the excursion the card is about", async () => {
    const ctx = createContext([HOME, WORK]);
    const { event, post } = createActionEvent(cardValue(WORK_TENANT, EXCURSION));

    await runWithContext(ctx.context, () => handler(event));

    const alerts = ctx.alertsBySlug.get("work-clinic")!;
    expect(alerts.acknowledgeAsLinkedMember).toHaveBeenCalledExactlyOnceWith(
      "link-work",
      asLinkedMember(EXCURSION),
    );
    expect(ctx.ambientAcknowledge).not.toHaveBeenCalled();
    expect(post).toHaveBeenCalledOnce();
    expect(postedText(post)).toContain(
      "Acknowledged for everyone by Sam Tester. Any other active alerts are untouched.",
    );
  });

  it("acknowledges the whole tenant for a value that names only a tenant", async () => {
    const ctx = createContext([HOME, WORK]);
    const { event, post } = createActionEvent(WORK_TENANT);

    await runWithContext(ctx.context, () => handler(event));

    const alerts = ctx.alertsBySlug.get("work-clinic")!;
    expect(alerts.acknowledgeAsLinkedMember).toHaveBeenCalledExactlyOnceWith(
      "link-work",
      asLinkedMember(null),
    );
    expect(post).toHaveBeenCalledOnce();
    expect(postedText(post)).toContain(
      "All alerts acknowledged for everyone by Sam Tester.",
    );
  });

  it("acknowledges the excursion named by a two-UUID value", async () => {
    const ctx = createContext([HOME, WORK]);
    const { event } = createActionEvent(`${WORK_TENANT}:${EXCURSION}`);

    await runWithContext(ctx.context, () => handler(event));

    expect(ctx.scopedApiFactory).toHaveBeenCalledExactlyOnceWith("work-clinic");
    expect(
      ctx.alertsBySlug.get("work-clinic")!.acknowledgeAsLinkedMember,
    ).toHaveBeenCalledExactlyOnceWith("link-work", asLinkedMember(EXCURSION));
  });

  it("acknowledges through the client scoped to the alert's tenant", async () => {
    const ctx = createContext([HOME, WORK]);
    const { event } = createActionEvent(cardValue(WORK_TENANT, EXCURSION));

    await runWithContext(ctx.context, () => handler(event));

    expect(ctx.resolve).toHaveBeenCalledExactlyOnceWith("discord", "discord-user-1");
    expect(ctx.scopedApiFactory).toHaveBeenCalledExactlyOnceWith("work-clinic");
    expect(ctx.alertsBySlug.has("home-clinic")).toBe(false);
    expect(ctx.ambientAcknowledge).not.toHaveBeenCalled();
  });

  it("falls back to the only link when the button carries no tenant", async () => {
    const ctx = createContext([HOME]);
    const { event, post } = createActionEvent();

    await runWithContext(ctx.context, () => handler(event));

    expect(ctx.scopedApiFactory).toHaveBeenCalledExactlyOnceWith("home-clinic");
    expect(
      ctx.alertsBySlug.get("home-clinic")!.acknowledgeAsLinkedMember,
    ).toHaveBeenCalledExactlyOnceWith("link-home", asLinkedMember(null));
    expect(post).toHaveBeenCalledOnce();
    expect(postedText(post)).toContain(
      "All alerts acknowledged for everyone by Sam Tester.",
    );
  });

  it("tells an unlinked user to connect and calls no api", async () => {
    const ctx = createContext([]);
    const { event, post, postEphemeral } = createActionEvent(
      cardValue(WORK_TENANT, EXCURSION),
    );

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
    const { event, post, postEphemeral } = createActionEvent(
      cardValue(WORK_TENANT, EXCURSION),
    );

    await runWithContext(ctx.context, () => handler(event));

    expect(postEphemeral).toHaveBeenCalledOnce();
    expect(ctx.scopedApiFactory).not.toHaveBeenCalled();
    expect(ctx.ambientAcknowledge).not.toHaveBeenCalled();
    expect(post).not.toHaveBeenCalled();
  });

  it("refuses a tenant the tapping user has no link to", async () => {
    const ctx = createContext([HOME]);
    const { event, post, postEphemeral } = createActionEvent(
      cardValue(WORK_TENANT, EXCURSION),
    );

    await runWithContext(ctx.context, () => handler(event));

    expect(postEphemeral).toHaveBeenCalledExactlyOnceWith(
      event.user,
      "That belongs to a Nocturne account you aren't linked to. Your linked accounts: `home`.",
      { fallbackToDM: true },
    );
    expect(ctx.scopedApiFactory).not.toHaveBeenCalled();
    expect(post).not.toHaveBeenCalled();
  });

  it("uses the tenant on the button over the user's default", async () => {
    const ctx = createContext([asDefault(HOME), WORK]);
    const { event, post } = createActionEvent(cardValue(WORK_TENANT, EXCURSION));

    await runWithContext(ctx.context, () => handler(event));

    expect(ctx.scopedApiFactory).toHaveBeenCalledExactlyOnceWith("work-clinic");
    expect(ctx.alertsBySlug.has("home-clinic")).toBe(false);
    expect(post).toHaveBeenCalledOnce();
    expect(postedText(post)).toContain(
      "Acknowledged for everyone by Sam Tester. Any other active alerts are untouched.",
    );
  });

  it("falls back to the default when the button carries no tenant", async () => {
    const ctx = createContext([HOME, asDefault(WORK)]);
    const { event, post, postEphemeral } = createActionEvent();

    await runWithContext(ctx.context, () => handler(event));

    expect(ctx.scopedApiFactory).toHaveBeenCalledExactlyOnceWith("work-clinic");
    expect(postEphemeral).not.toHaveBeenCalled();
    expect(post).toHaveBeenCalledOnce();
    expect(postedText(post)).toContain(
      "All alerts acknowledged for everyone by Sam Tester.",
    );
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

  it("refuses when two linked tenants answer to the same key", async () => {
    expect(encodeTenantKey(TWIN_TENANT_A)).toBe(encodeTenantKey(TWIN_TENANT_B));
    const ctx = createContext([TWIN_A, TWIN_B]);
    const { event, post, postEphemeral } = createActionEvent(
      cardValue(TWIN_TENANT_A, EXCURSION),
    );

    await runWithContext(ctx.context, () => handler(event));

    expect(postEphemeral).toHaveBeenCalledExactlyOnceWith(
      event.user,
      "You have multiple linked Nocturne accounts: `twin-a` (TWIN-A), `twin-b` (TWIN-B). Set a default in Settings → Integrations → Discord, or use the matching slash command with a label.",
      { fallbackToDM: true },
    );
    expect(ctx.scopedApiFactory).not.toHaveBeenCalled();
    expect(ctx.ambientAcknowledge).not.toHaveBeenCalled();
    expect(post).not.toHaveBeenCalled();
  });

  it.each(["nonsense", "*".repeat(22), "", ":"])(
    "acknowledges nothing when the excursion segment is %p",
    async (segment) => {
      const ctx = createContext([HOME, WORK]);
      const { event, post } = createActionEvent(
        `${encodeTenantKey(WORK_TENANT)}:${segment}`,
      );

      await runWithContext(ctx.context, () => handler(event));

      const alerts = ctx.alertsBySlug.get("work-clinic");
      expect(alerts?.acknowledgeAsLinkedMember).toBeUndefined();
      expect(ctx.ambientAcknowledge).not.toHaveBeenCalled();
      expect(post).toHaveBeenCalledExactlyOnceWith(
        "Couldn't tell which alert this button is for. Nothing was acknowledged.",
      );
    },
  );

  it("reports a failure without retrying against another tenant", async () => {
    const ctx = createContext([HOME, WORK]);
    const { event, post } = createActionEvent(cardValue(WORK_TENANT, EXCURSION));
    ctx.scopedApiFactory.mockImplementation(
      () =>
        ({
          alerts: {
            acknowledgeAsLinkedMember: vi.fn().mockRejectedValue(new Error("503")),
          },
        }) as unknown as BotApiClient,
    );

    await runWithContext(ctx.context, () => handler(event));

    expect(ctx.scopedApiFactory).toHaveBeenCalledExactlyOnceWith("work-clinic");
    expect(post).toHaveBeenCalledExactlyOnceWith(
      "Failed to acknowledge. Please try again.",
    );
  });

  it("says the alert was muted only for a member who cannot acknowledge for everyone", async () => {
    const ctx = createContext([HOME, WORK]);
    const { event, post } = createActionEvent(cardValue(WORK_TENANT, EXCURSION));
    await runWithContext(ctx.context, async () => {
      ctx.scopedApiFactory("work-clinic");
      ctx.alertsBySlug
        .get("work-clinic")!
        .acknowledgeAsLinkedMember.mockResolvedValue({ outcome: "muted" });
      await handler(event);
    });

    expect(
      ctx.alertsBySlug.get("work-clinic")!.acknowledgeAsLinkedMember,
    ).toHaveBeenCalledExactlyOnceWith("link-work", asLinkedMember(EXCURSION));
    expect(post).toHaveBeenCalledOnce();
    expect(cardTitle(post.mock.calls[0]?.[0])).toBe("Alert muted for you");
    expect(postedText(post)).toContain(
      "Muted for Sam Tester only. Everyone else is still alerted, because acknowledging for everyone needs permission to manage alerts.",
    );
    expect(postedText(post)).not.toContain("Acknowledged for everyone");
  });

  it("credits whoever already acknowledged, not the member who tapped", async () => {
    const ctx = createContext([HOME, WORK]);
    const { event, post } = createActionEvent(cardValue(WORK_TENANT, EXCURSION));
    await runWithContext(ctx.context, async () => {
      ctx.scopedApiFactory("work-clinic");
      ctx.alertsBySlug.get("work-clinic")!.acknowledgeAsLinkedMember.mockResolvedValue({
        outcome: "acknowledged",
        acknowledgedBy: "Alex Owner",
        alreadyAcknowledged: true,
      });
      await handler(event);
    });

    expect(cardTitle(post.mock.calls[0]?.[0])).toBe("Already acknowledged");
    expect(postedText(post)).toContain(
      "This alert was already acknowledged for everyone by Alex Owner.",
    );
    expect(postedText(post)).not.toContain("Sam Tester");
  });

  it("says an alert that already ended had nothing to acknowledge", async () => {
    const ctx = createContext([HOME, WORK]);
    const { event, post } = createActionEvent(cardValue(WORK_TENANT, EXCURSION));
    await runWithContext(ctx.context, async () => {
      ctx.scopedApiFactory("work-clinic");
      ctx.alertsBySlug
        .get("work-clinic")!
        .acknowledgeAsLinkedMember.mockResolvedValue({ outcome: "closed" });
      await handler(event);
    });

    expect(cardTitle(post.mock.calls[0]?.[0])).toBe("Nothing to acknowledge");
    expect(postedText(post)).toContain("This alert has already ended.");
  });

  it("does not report a failure when only the confirmation cannot be posted", async () => {
    const ctx = createContext([HOME, WORK]);
    const { event, post } = createActionEvent(cardValue(WORK_TENANT, EXCURSION));
    post.mockRejectedValue(new Error("channel_not_found"));

    await expect(
      runWithContext(ctx.context, () => handler(event)),
    ).resolves.toBeUndefined();

    expect(
      ctx.alertsBySlug.get("work-clinic")!.acknowledgeAsLinkedMember,
    ).toHaveBeenCalledOnce();
    expect(post).toHaveBeenCalledOnce();
  });
});

describe("/alerts command", () => {
  let handler: (event: SlashCommandEvent) => Promise<void>;

  beforeEach(() => {
    handler = registerHandlers().commands.get("/alerts")!;
  });

  const excursion = (id: string, ruleName: string): ActiveExcursion => ({
    id,
    ruleName,
    startedAt: new Date(Date.now() - 3 * 60_000).toISOString(),
  });

  it("lists every active excursion", async () => {
    const ctx = createContext(
      [asDefault(WORK)],
      [excursion("e1", "Urgent low"), excursion("e2", "High")],
    );
    const { event, post } = createSlashEvent();

    await runWithContext(ctx.context, () => handler(event));

    expect(
      ctx.alertsBySlug.get("work-clinic")!.getActiveAlerts,
    ).toHaveBeenCalledOnce();
    expect(cardFields(post.mock.calls[0]?.[0])).toEqual([
      "Urgent low: Firing, started 3 min ago",
      "High: Firing, started 3 min ago",
    ]);
  });

  it("says there are none rather than posting an empty card", async () => {
    const ctx = createContext([asDefault(WORK)]);
    const { event, post } = createSlashEvent();

    await runWithContext(ctx.context, () => handler(event));

    expect(post).toHaveBeenCalledExactlyOnceWith("No active alerts for WORK.");
  });

  it("reads a null response as no active alerts", async () => {
    const ctx = createContext([asDefault(WORK)], null);
    const { event, post } = createSlashEvent();

    await runWithContext(ctx.context, () => handler(event));

    expect(post).toHaveBeenCalledExactlyOnceWith("No active alerts for WORK.");
  });

  it("reports a failed lookup", async () => {
    const ctx = createContext([asDefault(WORK)]);
    const { event, post } = createSlashEvent();
    ctx.scopedApiFactory.mockImplementation(
      () =>
        ({
          alerts: {
            getActiveAlerts: vi.fn().mockRejectedValue(new Error("503")),
          },
        }) as unknown as BotApiClient,
    );

    await runWithContext(ctx.context, () => handler(event));

    expect(post).toHaveBeenCalledExactlyOnceWith(
      "Failed to fetch alerts. Please try again.",
    );
  });
});
