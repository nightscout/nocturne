import { describe, expect, it } from "vitest";
import { NotificationLabel } from "@nocturne/api-client";
import { resolveNotificationLabel } from "./notification-labels";

describe("resolveNotificationLabel", () => {
  it("words every label the backend files", () => {
    for (const label of Object.values(NotificationLabel)) {
      expect(resolveNotificationLabel(label), label).not.toBe(label);
    }
  });

  it("words the dismiss action of an external notice", () => {
    expect(resolveNotificationLabel(NotificationLabel.Dismiss)).toBe("Dismiss");
  });

  it("returns text that is not a label as it is", () => {
    expect(resolveNotificationLabel("Urgent low")).toBe("Urgent low");
    expect(resolveNotificationLabel("constructor")).toBe("constructor");
  });

  it("returns nothing for nothing", () => {
    expect(resolveNotificationLabel(undefined)).toBe("");
    expect(resolveNotificationLabel(null)).toBe("");
  });
});
