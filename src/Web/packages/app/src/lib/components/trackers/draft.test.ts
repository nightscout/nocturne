import { describe, expect, it } from "vitest";
import {
  NotificationUrgency,
  PumpCapability,
  TrackerCategory,
  TrackerMode,
} from "#lib/api/generated/nocturne-api-client.js";
import { beginLifespanEdit, draftFrom, holdsInsulin, newDraftFrom, postedThresholds, showsCovers } from "./draft";

const sensor = {
  id: "d1",
  name: "G7 sensor",
  category: TrackerCategory.Sensor,
  mode: TrackerMode.Duration,
  lifespanHours: 240,
  notificationThresholds: [
    { id: "t1", alertRuleId: "r1", urgency: NotificationUrgency.Warn, hours: -24, displayOrder: 0 },
    { id: "t2", alertRuleId: "r2", urgency: NotificationUrgency.Hazard, hours: 240, displayOrder: 1 },
    { id: "t3", alertRuleId: "r3", urgency: NotificationUrgency.Urgent, hours: 251, displayOrder: 2 },
  ],
};

describe("tracker draft", () => {
  it("keeps the thresholds of a definition under edit tied to their rules", () => {
    expect(draftFrom(sensor).notifications[0]).toMatchObject({ id: "t1", alertRuleId: "r1", hours: -24 });
  });

  it("starts a new definition with thresholds that belong to no rule", () => {
    const draft = newDraftFrom(sensor);

    expect(draft.notifications.map((n) => n.hours)).toEqual([-24, 240, 251]);
    expect(draft.notifications.every((n) => n.id === undefined && n.alertRuleId === undefined)).toBe(true);
  });

  it("moves the thresholds at and after the end of the lifespan with it", () => {
    const draft = newDraftFrom(sensor);

    beginLifespanEdit(draft)(336);

    expect(draft.lifespanHours).toBe(336);
    expect(draft.notifications.map((n) => n.hours)).toEqual([-24, 336, 347]);
  });

  it("moves them from where the edit found them, whatever the lifespan passes through", () => {
    const draft = newDraftFrom({
      ...sensor,
      notificationThresholds: [
        { urgency: NotificationUrgency.Info, hours: 100, displayOrder: 0 },
        { urgency: NotificationUrgency.Hazard, hours: 240, displayOrder: 1 },
      ],
    });
    const setLifespan = beginLifespanEdit(draft);

    for (const typed of [24, 2, undefined, 3, 33, 336]) setLifespan(typed);

    expect(draft.notifications.map((n) => n.hours)).toEqual([100, 336]);
  });

  it("does not move the thresholds of a tracker that had no lifespan", () => {
    const draft = newDraftFrom({ ...sensor, lifespanHours: undefined });

    beginLifespanEdit(draft)(72);

    expect(draft.lifespanHours).toBe(72);
    expect(draft.notifications.map((n) => n.hours)).toEqual([-24, 240, 251]);
  });

  it("does not post a threshold with no time", () => {
    const draft = newDraftFrom(sensor);
    draft.notifications = [...draft.notifications, { urgency: NotificationUrgency.Info, hours: undefined }];

    expect(postedThresholds(draft)).toHaveLength(3);
  });

  it("reads a pod that states nothing as holding insulin, and a sensor as replacing no pump part", () => {
    const pod = draftFrom({ category: TrackerCategory.Pod });
    const site = draftFrom({ category: TrackerCategory.Cannula, covers: [PumpCapability.Site] });

    expect(holdsInsulin(pod)).toBe(true);
    expect(holdsInsulin(site)).toBe(false);
    expect(showsCovers(draftFrom(sensor))).toBe(false);
  });
});
