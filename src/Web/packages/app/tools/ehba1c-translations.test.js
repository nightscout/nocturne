/* eslint-disable security/detect-non-literal-fs-filename -- Fixture files are confined to a fresh mkdtemp directory. */
import { copyFile, mkdtemp, mkdir, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { createRequire } from "node:module";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { wuchale } from "wuchale/vite";
import toRuntime from "wuchale/runtime";
import supportedLocales from "../../../supportedLocales.json";

const require = createRequire(import.meta.url);
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
          files: ["src/**/*.svelte"],
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
    }
  );
});
