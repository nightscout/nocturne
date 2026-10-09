/* eslint-disable security/detect-non-literal-fs-filename -- Fixture files are confined to a fresh mkdtemp directory. */
import {
  copyFile,
  mkdtemp,
  mkdir,
  readFile,
  rm,
  writeFile,
} from "node:fs/promises";
import { createRequire } from "node:module";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { wuchale } from "wuchale/vite";
import toRuntime from "wuchale/runtime";
import supportedLocales from "../../../supportedLocales.json";

const require = createRequire(import.meta.url);
const { po } = require("gettext-parser");
const googleHealthReference = /google-health|GoogleHealthSourceRow/;
const serverConnectorsReference = /ServerConnectorsCard/;
const placeholderPattern = /(<\/?\d+\s*\/?>|\{[^}]+\})/g;
const cursorResetFailureCopy =
  "One or more connectors failed; review the connector details and retry the failed range.";
const representativeCopy = [
  "Google Health",
  "Status unavailable",
  "Loading...",
  "Import recovery",
  "Open connector reset",
  "Save and connect",
  "Refresh inventory",
  "Disconnecting…",
  "Disconnecting Google Health. The current scan or import is being stopped.",
  "Google Health is disconnected. Your imported data has been kept.",
  "Historical import progress",
  "The requested history is complete.",
  "Each sync refreshes today and imports one older calendar month.",
  cursorResetFailureCopy,
  "Steps",
  "Heart rate",
  "Weight",
  "Sleep sessions and stages",
  "Active energy burned",
  "Total calories",
  "Distance",
  "Floors",
  "Workouts",
  "Body fat",
  "Height",
  "Nutrition log",
  "Hydration",
  "Blood glucose",
  "Oxygen saturation",
  "Heart rate variability",
  "Resting heart rate",
  "Daily oxygen saturation",
  "Daily respiratory rate",
  "Sleep respiratory rate",
  "Core body temperature",
  "Menstrual period",
  "Ovulation test",
  "Cervical mucus",
  "Sexual activity",
  "Unknown data type",
  "Google Health currently provides no read-only scope for this type.",
  "Not exposed as a readable Google Health API data type.",
];
const protectedProductNames = [
  "Google Health",
  "Google Cloud",
  "Google",
  "Fitbit",
  "Nocturne",
];

function entries(catalog) {
  return Object.values(catalog.translations).flatMap((group) =>
    Object.values(group)
  );
}

function placeholders(value) {
  return [...(value.match(placeholderPattern) ?? [])].sort().join("|");
}

function isGoogleHealthEntry(entry) {
  const reference = String(entry.comments?.reference ?? "");
  return (
    googleHealthReference.test(reference) ||
    entry.msgid === cursorResetFailureCopy ||
    (serverConnectorsReference.test(reference) &&
      /Google Health|Status unavailable|Import steps, heart rate, weight, and sleep from Google Health/.test(
        entry.msgid
      ))
  );
}

describe("Google Health production translations", () => {
  let root;
  let messageIds;
  let expectedPlaceholders;
  let disconnectMessageIds;

  beforeAll(async () => {
    root = await mkdtemp(
      join(tmpdir(), "nocturne-google-health-translations-")
    );
    await mkdir(join(root, "locales"));
    await writeFile(join(root, "package.json"), '{"type":"module"}');
    for (const locale of supportedLocales) {
      await copyFile(
        new URL(`../../../locales/${locale}.po`, import.meta.url),
        join(root, "locales", `${locale}.po`)
      );
    }
    expectedPlaceholders = new Map(
      entries(
        po.parse(
          await readFile(new URL("../../../locales/en.po", import.meta.url))
        )
      )
        .filter((entry) => entry.msgid && isGoogleHealthEntry(entry))
        .map((entry) => [entry.msgid, placeholders(entry.msgid)])
    );
    const configPath = join(root, "wuchale.config.js");
    await writeFile(
      configPath,
      `
      import { adapter } from ${JSON.stringify(pathToFileURL(require.resolve("@wuchale/svelte")).href)};
      import { defineConfig, pofile } from ${JSON.stringify(pathToFileURL(require.resolve("wuchale")).href)};
      export default defineConfig({
        locales: ${JSON.stringify(supportedLocales)},
        localesDir: "locales",
        adapters: { main: adapter({
          sourceLocale: "en",
          loader: "sveltekit",
          storage: pofile({ location: "locales/{locale}.po" }),
          files: ["src/**/*.svelte"],
        }) },
      });
    `
    );
    const plugin = wuchale({ configPath });
    await plugin.configResolved({ env: { DEV: false } });
    const fixture = representativeCopy
      .map((copy) =>
        copy === cursorResetFailureCopy
          ? `<span>{errorCode === "connector_reset_failed" ? ${JSON.stringify(copy)} : errorCode}</span>`
          : `<span>${copy}</span>`
      )
      .join("");
    const { code } = await plugin.transform.handler(
      fixture,
      join(
        root,
        "src/routes/(authenticated)/settings/connectors/google-health/google-health-page.svelte"
      ),
      { ssr: false }
    );
    messageIds = [...code.matchAll(/_w_runtime_\((\d+)\)/g)].map((match) =>
      Number(match[1])
    );
    expect(messageIds).toHaveLength(representativeCopy.length);
    expect(code).toMatch(/errorCode\s*===\s*["']connector_reset_failed["']/);
    const pageSource = await readFile(
      new URL(
        "../src/routes/(authenticated)/settings/connectors/google-health/google-health-page.svelte",
        import.meta.url
      ),
      "utf8"
    );
    const buttons = [...pageSource.matchAll(/<Button\b[\s\S]*?<\/Button>/g)]
      .map(([button]) => button)
      .filter((button) => button.includes("run(disconnect)"));
    expect(buttons).toHaveLength(2);
    const transformed = await plugin.transform.handler(
      buttons.join(""),
      join(
        root,
        "src/routes/(authenticated)/settings/connectors/google-health/disconnect-buttons.svelte"
      ),
      { ssr: false }
    );
    disconnectMessageIds = [
      ...transformed.code.matchAll(/_w_runtime_\.c\((\d+)/g),
    ].map((match) => Number(match[1]));
    expect(disconnectMessageIds).toHaveLength(4);
  });

  afterAll(async () => {
    if (root) await rm(root, { recursive: true, force: true });
  });

  it.each(supportedLocales)(
    "keeps Google Health copy visible in %s",
    async (locale) => {
      const catalogUrl = pathToFileURL(
        join(root, "locales/.wuchale", `main.0.${locale}.compiled.js`)
      );
      const catalog = await import(/* @vite-ignore */ catalogUrl.href);
      const runtime = toRuntime(catalog, locale);
      expect(
        messageIds
          .map((id) => runtime(id))
          .every((text) => text.trim().length > 0)
      ).toBe(true);
      for (const id of disconnectMessageIds) {
        expect(
          runtime
            .c(id)
            .flat(Infinity)
            .filter((part) => typeof part === "string")
            .join("")
            .trim().length
        ).toBeGreaterThan(0);
      }
    }
  );

  it.each(supportedLocales)(
    "has no empty Google Health catalog entries in %s",
    async (locale) => {
      const catalog = po.parse(
        await readFile(
          new URL(`../../../locales/${locale}.po`, import.meta.url)
        )
      );
      const localized = new Map(
        entries(catalog).map((entry) => [entry.msgid, entry])
      );
      const missing = [...expectedPlaceholders.keys()].filter(
        (id) => !localized.get(id)?.msgstr?.[0]?.trim()
      );
      expect(missing).toEqual([]);
      const mismatches = entries(catalog).filter(
        (entry) =>
          expectedPlaceholders.has(entry.msgid) &&
          placeholders(entry.msgstr?.[0] ?? "") !==
            expectedPlaceholders.get(entry.msgid)
      );
      expect(mismatches).toEqual([]);
    }
  );

  it.each(supportedLocales)(
    "keeps connector product names untranslated in %s",
    async (locale) => {
      const catalog = po.parse(
        await readFile(
          new URL(`../../../locales/${locale}.po`, import.meta.url)
        )
      );
      const translatedProductName = entries(catalog).filter((entry) => {
        if (!entry.msgid || !isGoogleHealthEntry(entry)) return false;
        const translation = String(entry.msgstr?.[0] ?? "");
        return protectedProductNames.some(
          (name) => entry.msgid.includes(name) && !translation.includes(name)
        );
      });
      expect(translatedProductName).toEqual([]);
    }
  );
});
