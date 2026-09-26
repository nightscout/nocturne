import { beforeAll, describe, expect, it } from "vitest";
import { postEntries, sgvSeries } from "../helpers/data.ts";
import { eventually } from "../helpers/http.ts";
import { seedTenant, type Tenant } from "../helpers/tenant.ts";

interface AlertRule {
  id: string;
  name: string;
}

interface ActiveExcursion {
  id: string;
  alertRuleId: string;
  snoozedUntil: string | null;
  acknowledgedAt: string | null;
  activeInstances: Array<{ id: string; status: string; snoozedUntil: string | null; snoozeCount: number }>;
}

interface InAppNotification {
  id: string;
  type: string;
  title: string;
  subtitle: string;
  sourceId: string;
}

// The sweep that lifts an expired snooze runs every 30 seconds (AlertSweepService), and a snooze
// is whole minutes, so resumption is observed within snooze + one sweep + slack.
const RESUME_TIMEOUT = 60_000 + 30_000 + 20_000;

describe("alerts", () => {
  let tenant: Tenant;
  let rule: AlertRule;
  const ruleName = "E2E urgent low";

  const notifications = async () => {
    const res = await tenant.api.get<InAppNotification[]>("/api/v4/notifications");
    return res.body.filter((n) => n.type === "alert.firing" && n.title === ruleName);
  };
  const active = async () => {
    const res = await tenant.api.get<ActiveExcursion[]>("/api/v4/alerts/active");
    return res.body.filter((e) => e.alertRuleId === rule.id);
  };

  beforeAll(async () => {
    tenant = await seedTenant();
    rule = await tenant.api.ok<AlertRule>("POST", "/api/v4/alert-rules", {
      name: ruleName,
      conditionType: "threshold",
      conditionParams: { value: 70, direction: "below" },
      severity: "critical",
      channels: [{ channelType: "in_app", destination: tenant.subjectId }],
    });
    await postEntries(tenant.api, sgvSeries({ count: 3, end: Date.now() - 5 * 60_000, valueAt: () => 120 }));
  });

  it("stays quiet while glucose is in range", async () => {
    expect(await active()).toEqual([]);
    expect(await notifications()).toEqual([]);
  });

  it("fires when a reading crosses the threshold and notifies the destination", async () => {
    await postEntries(tenant.api, sgvSeries({ count: 1, valueAt: () => 52 }));

    const [excursion] = await eventually(async () => {
      const list = await active();
      return list.length > 0 ? list : undefined;
    }, { what: "an active excursion" });
    expect(excursion!.activeInstances).toHaveLength(1);
    expect(excursion!.activeInstances[0]!.status).toBe("triggered");

    const [notification] = await eventually(async () => {
      const list = await notifications();
      return list.length > 0 ? list : undefined;
    }, { what: "an in-app notification" });
    expect(notification!.sourceId).toBe(excursion!.id);
    expect(notification!.subtitle).toContain("52");
  });

  it("snoozes, suppresses, and resumes when the snooze expires", { timeout: RESUME_TIMEOUT + 30_000 }, async () => {
    const [excursion] = await active();
    const instance = excursion!.activeInstances[0]!;
    const notifiedBefore = (await notifications()).length;

    const snooze = await tenant.api.post(`/api/v4/alerts/instances/${instance.id}/snooze`, { minutes: 1 });
    expect(snooze.status).toBe(204);
    const snoozedAt = Date.now();

    const [snoozed] = await active();
    const until = Date.parse(snoozed!.snoozedUntil!);
    expect(until - snoozedAt).toBeGreaterThan(45_000);
    expect(until - snoozedAt).toBeLessThanOrEqual(61_000);
    expect(snoozed!.activeInstances[0]!.snoozeCount).toBe(1);

    // Still low while snoozed: the alert stays active but nothing new is delivered.
    await postEntries(tenant.api, sgvSeries({ count: 1, valueAt: () => 50 }));
    await new Promise((r) => setTimeout(r, 5_000));
    expect((await notifications()).length).toBe(notifiedBefore);
    expect((await active()).length).toBe(1);

    const resumed = await eventually(async () => {
      const list = await notifications();
      return list.length > notifiedBefore ? list : undefined;
    }, { what: "a re-notification after the snooze lapsed", timeoutMs: RESUME_TIMEOUT, intervalMs: 2_000 });
    expect(Date.now()).toBeGreaterThanOrEqual(until);
    expect(resumed.length).toBe(notifiedBefore + 1);

    const [after] = await active();
    expect(after!.snoozedUntil).toBeNull();
  });

  it("acknowledging records who acknowledged, and the excursion stays open while still low", async () => {
    const [excursion] = await active();
    const ack = await tenant.api.post(`/api/v4/alerts/excursions/${excursion!.id}/acknowledge`, {});
    expect(ack.status).toBeLessThan(300);

    const [after] = await active();
    expect(after!.id).toBe(excursion!.id);
    expect(after!.acknowledgedAt).not.toBeNull();
  });
});
