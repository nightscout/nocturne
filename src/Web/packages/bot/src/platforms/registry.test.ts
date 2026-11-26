import { describe, expect, it } from "vitest";
import { PLATFORMS, channelTarget, defaultLinkSetting, platformName, resolvePlatforms } from "./registry.js";

describe("the platform registry", () => {
  it("delivers each alert channel type on one platform", () => {
    const types = PLATFORMS.flatMap((p) => Object.keys(p.channelTypes));

    expect(new Set(types).size).toBe(types.length);
    expect(channelTarget("telegram_group")).toMatchObject({ platform: { name: "telegram" }, kind: "channel" });
    expect(channelTarget("resend_email")).toMatchObject({ platform: { name: "resend" }, kind: "direct" });
    expect(channelTarget("webhook")).toBeUndefined();
  });

  it("names a platform as its users know it, and an unknown one by its adapter name", () => {
    expect(platformName("whatsapp")).toBe("WhatsApp");
    expect(platformName("matrix")).toBe("Matrix");
  });

  it("points to a default-link setting only where there is a page for it", () => {
    expect(defaultLinkSetting("discord")).toContain("Settings → Integrations → Discord");
    expect(defaultLinkSetting("telegram")).toBeNull();
  });

  it("takes Meta's GET verification only on the WhatsApp webhook", () => {
    expect(Object.fromEntries(PLATFORMS.map((p) => [p.name, p.webhookMethods]))).toEqual({
      discord: ["POST"],
      slack: ["POST"],
      telegram: ["POST"],
      whatsapp: ["GET", "POST"],
      resend: ["POST"],
    });
  });

  it("takes commands typed in a direct message only on WhatsApp", () => {
    expect(PLATFORMS.filter((p) => p.commands === "typed").map((p) => p.name)).toEqual(["whatsapp"]);
  });
});

describe("resolvePlatforms", () => {
  const env = { TELEGRAM_BOT_TOKEN: "env-token", DISCORD_BOT_TOKEN: "env-token" };

  it("runs a platform with its stored settings", () => {
    const platforms = resolvePlatforms(
      [{ category: "telegram", enabled: true, fields: { botToken: "stored", webhookSecretToken: "secret" } }],
      env,
    );

    expect(platforms.telegram).toEqual({ enabled: true, botToken: "stored", webhookSecretToken: "secret" });
  });

  it("turns off a platform whose stored settings are disabled, whatever the environment holds", () => {
    expect(resolvePlatforms([{ category: "telegram", enabled: false, fields: {} }], env).telegram).toBe(false);
  });

  it("runs a platform with no stored settings when the environment configures it", () => {
    const platforms = resolvePlatforms([{ category: "telegram", enabled: true, fields: {} }], env);

    expect(platforms.discord).toBe(true);
    expect(platforms.slack).toBe(false);
  });

  it("reads every platform from the environment when the stored settings could not be read", () => {
    expect(resolvePlatforms(null, env)).toEqual({
      discord: true,
      slack: false,
      telegram: true,
      whatsapp: false,
      resend: false,
    });
  });
});
