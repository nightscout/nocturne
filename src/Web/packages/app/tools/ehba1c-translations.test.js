/* eslint-disable security/detect-non-literal-fs-filename -- Fixture files are confined to a fresh mkdtemp directory. */
import {
  copyFile,
  mkdtemp,
  mkdir,
  readFile,
  rm,
  writeFile,
} from "node:fs/promises";
import { tmpdir } from "node:os";
import { createRequire } from "node:module";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { wuchale } from "wuchale/vite";
import toRuntime from "wuchale/runtime";
import supportedLocales from "../../../supportedLocales.json";

const require = createRequire(import.meta.url);
const PO = createRequire(require.resolve("wuchale"))("pofile");
const sourceFiles = [
  "lib/stores/appearance-store.svelte.ts",
  "lib/navigation/report-navigation.svelte.ts",
  "lib/components/rbac/PermissionCategorySelector.svelte",
  "routes/(authenticated)/settings/appearance/+page.svelte",
  ...[
    "",
    "agp/",
    "idp/",
    "comparison/",
    "executive-summary/",
    "glucose-distribution/",
    "ehba1c/",
  ].map((report) => `routes/(authenticated)/reports/${report}+page.svelte`),
];
const placeholders = (text) =>
  [...text.matchAll(/\{\d+\}|<\/?\d+\/?>/g)].map(([token]) => token).sort();
const labLabels = {
  en: "Lab result",
  es: "Resultado de laboratorio",
  fr: "Résultat de laboratoire",
  de: "Laborergebnis",
  it: "Risultato di laboratorio",
  pt: "Resultado laboratorial",
  nl: "Labresultaat",
  ru: "Результат лабораторного анализа",
  zh: "实验室检测结果",
  ja: "検査結果",
  ko: "검사 결과",
};

// The browser component suite does not apply Wuchale. Missing catalogue entries
// still get IDs during a production transform, but resolve to empty text at runtime.
describe("eHbA1c tooltip production translations", () => {
  let root;
  let messageIds;
  let productionIds;
  let a1cMessages;

  beforeAll(async () => {
    root = await mkdtemp(join(tmpdir(), "nocturne-ehba1c-translations-"));
    await mkdir(join(root, "locales"));
    await writeFile(join(root, "package.json"), '{"type":"module"}');
    for (const locale of supportedLocales) {
      await copyFile(
        new URL(`../../../locales/${locale}.po`, import.meta.url),
        join(root, "locales", `${locale}.po`)
      );
    }
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
          files: ["src/**/*.svelte", "src/**/*.svelte.ts"],
        }) },
      });
    `
    );
    const plugin = wuchale({ configPath });
    await plugin.configResolved({ env: { DEV: false } });
    const { code } = await plugin.transform.handler(
      "<span>eHbA1c</span><span>Lab result</span>",
      join(root, "src/routes/(authenticated)/reports/ehba1c/+page.svelte"),
      { ssr: false }
    );
    messageIds = [...code.matchAll(/_w_runtime_\((\d+)\)/g)].map((match) =>
      Number(match[1])
    );
    expect(messageIds).toHaveLength(2);
    productionIds = new Set();
    for (const file of sourceFiles) {
      const source = await readFile(
        new URL(`../src/${file}`, import.meta.url),
        "utf8"
      );
      const transformed = await plugin.transform.handler(
        source,
        join(root, "src", file),
        { ssr: false }
      );
      for (const [, id] of [
        ...transformed.code.matchAll(/_w_runtime_\((\d+)/g),
        ...transformed.code.matchAll(/_w_runtime_\.[a-z]+\((\d+)/g),
      ]) {
        productionIds.add(Number(id));
      }
      if (file.includes("settings/appearance")) {
        // Translated select values can persist an empty name and leave only the estimate's e prefix.
        expect(transformed.code).toContain(
          'value === "HbA1c" || value === "A1c"'
        );
        for (const [value, label] of [
          ["HbA1c", "HbA1c"],
          ["A1c", "A1c"],
          ["percent", "% (NGSP)"],
          ["mmol/mol", "mmol/mol (IFCC)"],
        ]) {
          expect(transformed.code).toContain(
            `<SelectItem value="${value}">${label}</SelectItem>`
          );
        }
      }
      if (file.includes("appearance-store")) {
        expect(transformed.code).toContain(
          'const A1C_NAMES = ["HbA1c", "A1c"] as const'
        );
        expect(transformed.code).toContain('"nocturne-a1c-name", "HbA1c"');
      }
    }
    const english = PO.parse(
      await readFile(new URL("../../../locales/en.po", import.meta.url), "utf8")
    );
    a1cMessages = english.items.filter(
      (item) =>
        item.references.some((ref) =>
          sourceFiles.some((file) => ref === `src/${file}`)
        ) &&
        (item.extractedComments.some((comment) =>
          comment.includes("a1cLabel")
        ) ||
          [
            "A1c display name",
            "A1c display unit",
            "Estimated values retain the e prefix: eHbA1c or eA1c.",
          ].includes(item.msgid) ||
          item.msgid.startsWith("Use % (NGSP)") ||
          item.msgid.startsWith("Target: <{0}") ||
          item.msgid.startsWith("Target: below {0}"))
    );
    expect(a1cMessages).toHaveLength(18);
  });

  afterAll(async () => {
    if (root) await rm(root, { recursive: true, force: true });
  });

  it.each(supportedLocales)(
    "keeps both labels visible in %s",
    async (locale) => {
      const catalogUrl = pathToFileURL(
        join(root, "locales/.wuchale", `main.0.${locale}.compiled.js`)
      );
      const catalog = await import(/* @vite-ignore */ catalogUrl.href);
      const runtime = toRuntime(catalog, locale);
      expect(messageIds.map((id) => runtime(id))).toEqual([
        "eHbA1c",
        labLabels[locale],
      ]);
      for (const id of productionIds) {
        expect(
          catalog.c[id],
          `${locale}: production message ${id}`
        ).toBeDefined();
        expect(
          JSON.stringify(catalog.c[id]),
          `${locale}: empty production message ${id}`
        ).not.toBe('""');
      }
      const po = PO.parse(
        await readFile(
          new URL(`../../../locales/${locale}.po`, import.meta.url),
          "utf8"
        )
      );
      for (const source of a1cMessages) {
        const translation = po.items.find(
          (item) =>
            item.msgid === source.msgid && item.msgctxt === source.msgctxt
        )?.msgstr[0];
        expect(translation?.trim(), `${locale}: ${source.msgid}`).toBeTruthy();
        expect(placeholders(translation)).toEqual(placeholders(source.msgid));
      }
    }
  );
});
