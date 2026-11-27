import { describe, expect, it } from "vitest";
import { AlertReceiptActorKind, type AlertReceiptResponse } from "@nocturne/api-client";
import { receiptActor, seenBy } from "./receipts.svelte";

const seenAt = new Date().toISOString();

const publicLink: AlertReceiptResponse = { actorKind: AlertReceiptActorKind.PublicLink, seenAt };
const guest: AlertReceiptResponse = { actorKind: AlertReceiptActorKind.Guest, actorLabel: "Grandma", seenAt };
const clock: AlertReceiptResponse = { actorKind: AlertReceiptActorKind.Clock, actorLabel: "Kitchen", seenAt };
const member: AlertReceiptResponse = { actorKind: AlertReceiptActorKind.Member, actorLabel: "Alex", seenAt };

describe("receipts", () => {
  it("names each kind of viewer, with a fallback for one with no label", () => {
    expect(receiptActor(publicLink)).toBe("Public link");
    expect(receiptActor(guest)).toBe("Grandma (guest link)");
    expect(receiptActor({ ...guest, actorLabel: undefined })).toBe("Guest (guest link)");
    expect(receiptActor(clock)).toBe("Kitchen (clock)");
    expect(receiptActor({ ...clock, actorLabel: undefined })).toBe("Clock (clock)");
    expect(receiptActor(member)).toBe("Alex");
    expect(receiptActor({ ...member, actorLabel: undefined })).toBe("A member");
  });

  it("is null when nobody has seen the alarm", () => {
    expect(seenBy(undefined)).toBeNull();
    expect(seenBy([])).toBeNull();
  });

  it("lists the viewers in one sentence with the language's own list words", () => {
    expect(seenBy([publicLink])).toBe("Seen by Public link");
    expect(seenBy([publicLink, guest])).toBe("Seen by Public link and Grandma (guest link)");
    expect(seenBy([publicLink, guest, clock])).toBe("Seen by Public link, Grandma (guest link), and Kitchen (clock)");
  });
});
