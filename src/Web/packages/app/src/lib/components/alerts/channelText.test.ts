import { describe, it, expect } from "vitest";
import { ChannelType } from "$api-clients";
import { CHANNEL_META, findChannelMeta } from "./channelMeta";
import {
  channelDescription,
  channelDestinationHelper,
  channelDestinationLabel,
  channelDestinationPatternMessage,
  channelLabel,
  destinationError,
} from "./channelText.svelte";

const ACRONYMS = new Set(["DM", "ID", "URL", "POST", "Nocturne"]);

function isSentenceCase(text: string): boolean {
  return text
    .split(/[\s-]+/)
    .slice(1)
    .every(
      (word) =>
        ACRONYMS.has(word.replace(/[^A-Za-z]/g, "")) ||
        !/^[A-Z][a-z]/.test(word)
    );
}

const texts = CHANNEL_META.flatMap((m) =>
  [
    { id: `${m.type} label`, text: channelLabel(m.type) },
    { id: `${m.type} description`, text: channelDescription(m.type) },
    { id: `${m.type} destination label`, text: channelDestinationLabel(m.type) },
    { id: `${m.type} helper`, text: channelDestinationHelper(m.type) },
    {
      id: `${m.type} pattern message`,
      text: channelDestinationPatternMessage(m.type),
    },
  ].flatMap((t) => (t.text === undefined ? [] : [{ id: t.id, text: t.text }]))
);

describe("channelText", () => {
  it("never returns the raw kind", () => {
    for (const m of CHANNEL_META) {
      expect(channelLabel(m.type)).not.toBe(m.type);
      expect(channelDescription(m.type)).not.toBe(m.type);
    }
  });

  it("starts every text with an uppercase letter so Wuchale extracts it", () => {
    for (const { id, text } of texts) {
      expect(text, id).toMatch(/^[A-Z]/);
    }
  });

  it("uses sentence case for labels", () => {
    for (const { id, text } of texts.filter((t) => t.id.endsWith("label"))) {
      expect(isSentenceCase(text), `${id}: ${text}`).toBe(true);
    }
  });

  it("spells the in-app channel in sentence case", () => {
    expect(channelLabel(ChannelType.InApp)).toBe("In-app");
  });

  it("has a pattern message for every destination that is pattern checked", () => {
    for (const m of CHANNEL_META.filter((m) => m.destinationPattern)) {
      expect(channelDestinationPatternMessage(m.type), m.type).toBeDefined();
    }
  });

  describe("destinationError", () => {
    it("is null with no channel", () => {
      expect(destinationError(undefined, "")).toBeNull();
    });

    it("names the field when the destination is empty", () => {
      const meta = findChannelMeta("webhook");
      expect(destinationError(meta, "  ")).toBe("Webhook URL is required.");
    });

    it("gives the pattern message for a destination that does not match", () => {
      const meta = findChannelMeta("discord_channel");
      expect(destinationError(meta, "not-a-snowflake")).toBe(
        "A channel ID is 17-20 digits."
      );
    });

    it("accepts a destination that matches", () => {
      const meta = findChannelMeta("discord_channel");
      expect(destinationError(meta, "1234567890123456789")).toBeNull();
    });
  });
});
