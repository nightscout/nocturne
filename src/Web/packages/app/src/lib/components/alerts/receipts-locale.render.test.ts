import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { AlertReceiptActorKind, type AlertReceiptResponse } from "@nocturne/api-client";
import { renderInLanguage } from "#lib/test-fixtures/render-in-language.js";
import { seedCatalogue } from "#lib/test-stubs/catalogue-seed.js";
import { seenBy } from "./receipts.svelte.js";

const seenAt = new Date().toISOString();
const receipts: AlertReceiptResponse[] = [
  { actorKind: AlertReceiptActorKind.PublicLink, seenAt },
  { actorKind: AlertReceiptActorKind.Guest, actorLabel: "Grandma", seenAt },
  { actorKind: AlertReceiptActorKind.Clock, actorLabel: "Kitchen", seenAt },
];

let restore: () => void;

beforeAll(async () => {
  restore = await seedCatalogue([
    { text: "Seen by {0}", de: "Gesehen von {0}" },
    { text: "Public link", de: "Oeffentlicher Link" },
    { text: "{0} (guest link)", de: "{0} (Gastlink)" },
    { text: "{0} (clock)", de: "{0} (Uhr)" },
  ]);
}, 120_000);

afterAll(() => restore());

const sentence = (language: "en" | "de") => renderInLanguage(language, { read: () => seenBy(receipts) ?? "" });

describe("who has seen an alarm, under the production translation transform", () => {
  it("is one translated message, and each kind of viewer is another", async () => {
    expect(await sentence("de")).toBe("Gesehen von Oeffentlicher Link, Grandma (Gastlink) und Kitchen (Uhr)");
  });

  it("keeps the English words and the English list in English", async () => {
    expect(await sentence("en")).toBe("Seen by Public link, Grandma (guest link), and Kitchen (clock)");
  });
});
