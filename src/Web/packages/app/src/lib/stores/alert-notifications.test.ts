import { beforeEach, describe, expect, it, vi } from "vitest";
import type { InAppNotificationDto } from "$lib/api";

const created = vi.hoisted(() => ({ close: vi.fn(), create: vi.fn() }));
vi.mock("$lib/audio/alarm-sounds", () => ({
  createSystemNotification: created.create,
}));
vi.mock("$lib/websocket/websocket-client.svelte", () => ({ WebSocketClient: class {} }));

import { closeAlertNotification, raiseAlertNotification } from "./alert-notifications.svelte";

const alert = (id: string, type = "alert.firing") =>
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

  it("closes an alert's notification when the alert is archived", () => {
    raiseAlertNotification(alert("c"));

    closeAlertNotification(alert("c"));

    expect(created.close).toHaveBeenCalledOnce();
  });
});
