import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  InAppNotificationType,
  NotificationLabel,
  type InAppNotificationDto,
} from "@nocturne/api-client";

const created = vi.hoisted(() => ({ close: vi.fn(), create: vi.fn() }));
vi.mock("#lib/audio/alarm-sounds.js", () => ({
  createSystemNotification: created.create,
}));
vi.mock("#lib/websocket/websocket-client.svelte.js", () => ({ WebSocketClient: class {} }));

import { closeAlertNotification, raiseAlertNotification } from "./alert-notifications.svelte";

const alert = (id: string, type: string = InAppNotificationType.AlertFiring) =>
  ({ id, type, title: "Urgent low", subtitle: "52" }) as InAppNotificationDto;

describe("alert notifications", () => {
  beforeEach(() => {
    created.close.mockReset();
    created.create.mockReset().mockReturnValue({ close: created.close });
  });

  it("raises an alert once, with sound, and nothing else", () => {
    raiseAlertNotification(alert("a"));
    raiseAlertNotification(alert("a"));
    raiseAlertNotification(alert("b", "tracker.due"));

    expect(created.create).toHaveBeenCalledOnce();
    expect(created.create).toHaveBeenCalledWith("Urgent low", "52", "alert-a", false);
  });

  it("raises the notice that a rule cannot run, with its text", () => {
    raiseAlertNotification({
      id: "n",
      type: InAppNotificationType.AlertUnevaluable,
      title: "Urgent low",
      subtitle: NotificationLabel.AlertUnevaluableSubtitle,
    } as InAppNotificationDto);

    expect(created.create).toHaveBeenCalledWith(
      "Urgent low",
      "This rule cannot run as set, so it sends no alerts. Open the rule, check its settings, and save it.",
      "alert-n",
      false
    );
  });

  it("closes an alert's notification when the alert is archived", () => {
    raiseAlertNotification(alert("c"));

    closeAlertNotification(alert("c"));

    expect(created.close).toHaveBeenCalledOnce();
  });
});
