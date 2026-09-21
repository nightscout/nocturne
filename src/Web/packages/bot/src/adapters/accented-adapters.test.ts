import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { toCardElement } from "chat";
import type { CardElement } from "chat";
import { AccentedDiscordAdapter } from "./accented-discord.js";
import { AccentedSlackAdapter } from "./accented-slack.js";
import { AlertCard } from "../cards/alert.js";
import { withAlertAccent } from "../lib/severity.js";
import type { AlertPayload } from "../types.js";

const payload = (severity: unknown) =>
  ({
    tenantId: "018f2a1b-3c4d-7000-8000-a1b2c3d4e5f6",
    excursionId: "33333333-3333-3333-3333-333333333333",
    ruleName: "Urgent low",
    subjectName: "Alex",
    glucoseValue: 54,
    trend: "SingleDown",
    trendRate: -1.4,
    readingTimestamp: "2026-01-01T00:00:00.000Z",
    severity,
  }) as unknown as AlertPayload;

const card = (severity: unknown): CardElement =>
  toCardElement(AlertCard({ payload: payload(severity) }))!;

const CRITICAL_RGB = 0xe7000b;
const WARNING_RGB = 0xf54900;
const DISCORD_DEFAULT_RGB = 5793266;

describe("AccentedDiscordAdapter", () => {
  let fetchMock: ReturnType<typeof vi.fn>;

  const adapter = () =>
    new AccentedDiscordAdapter({
      botToken: "bot-token",
      publicKey: "a".repeat(64),
      applicationId: "app-id",
    });

  const postedEmbed = () => {
    const body = JSON.parse(String(fetchMock.mock.calls.at(-1)?.[1]?.body));
    return body.embeds[0];
  };

  beforeEach(() => {
    fetchMock = vi.fn(async () =>
      new Response(JSON.stringify({ id: "discord-message-1" }), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      }),
    );
    vi.stubGlobal("fetch", fetchMock);
  });

  afterEach(() => vi.unstubAllGlobals());

  it("colours a critical alert with the critical status colour", async () => {
    await withAlertAccent("critical", () =>
      adapter().postMessage("discord:guild-1:channel-1", card("critical")),
    );

    expect(postedEmbed().color).toBe(CRITICAL_RGB);
  });

  it("gives a warning a different colour from a critical", async () => {
    await withAlertAccent("warning", () =>
      adapter().postMessage("discord:guild-1:channel-1", card("warning")),
    );

    expect(postedEmbed().color).toBe(WARNING_RGB);
    expect(postedEmbed().color).not.toBe(CRITICAL_RGB);
  });

  it("leaves the adapter's own colour alone for an unrecognised severity", async () => {
    await withAlertAccent("meltdown", () =>
      adapter().postMessage("discord:guild-1:channel-1", card("meltdown")),
    );

    expect(postedEmbed().color).toBe(DISCORD_DEFAULT_RGB);
  });

  it("leaves every message posted outside an alert untouched", async () => {
    await adapter().postMessage("discord:guild-1:channel-1", card("critical"));

    expect(postedEmbed().color).toBe(DISCORD_DEFAULT_RGB);
  });
});

describe("AccentedSlackAdapter", () => {
  const postMessage = vi.fn(async () => ({ ok: true, ts: "1700000000.000100" }));

  const adapter = () => {
    const slack = new AccentedSlackAdapter({
      botToken: "xoxb-token",
      signingSecret: "signing-secret",
    });
    (slack as unknown as { _client: unknown })._client = {
      chat: { postMessage },
    };
    return slack;
  };

  beforeEach(() => postMessage.mockClear());

  const posted = () => postMessage.mock.calls.at(-1)?.[0] as Record<string, any>;

  it("wraps a critical alert in an attachment carrying the critical colour", async () => {
    await withAlertAccent("critical", () =>
      adapter().postMessage("slack:C123", card("critical")),
    );

    expect(posted().attachments).toHaveLength(1);
    expect(posted().attachments[0].color).toBe("#e7000b");
    expect(posted().attachments[0].blocks.length).toBeGreaterThan(0);
  });

  it("gives a warning a different colour from a critical", async () => {
    await withAlertAccent("warning", () =>
      adapter().postMessage("slack:C123", card("warning")),
    );

    expect(posted().attachments[0].color).toBe("#f54900");
  });

  it("posts plain blocks for an unrecognised severity", async () => {
    await withAlertAccent("meltdown", () =>
      adapter().postMessage("slack:C123", card("meltdown")),
    );

    expect(posted().attachments).toBeUndefined();
    expect(posted().blocks.length).toBeGreaterThan(0);
  });

  it("maps a Slack rate limit onto the typed error the retry path expects", async () => {
    postMessage.mockRejectedValueOnce(
      Object.assign(new Error("An API error occurred: ratelimited"), {
        code: "slack_webapi_platform_error",
        data: { error: "ratelimited" },
      }),
    );

    // The adapter bundles its own error class, so this asserts on the type it
    // raises rather than on an identity imported from `chat`.
    const raised = await withAlertAccent("critical", () =>
      adapter().postMessage("slack:C123", card("critical")),
    ).catch((error: Error) => error);

    expect(raised.constructor.name).toBe("AdapterRateLimitError");
  });

  it("leaves every message posted outside an alert untouched", async () => {
    await adapter().postMessage("slack:C123", card("critical"));

    expect(posted().attachments).toBeUndefined();
    expect(posted().blocks.length).toBeGreaterThan(0);
  });
});
