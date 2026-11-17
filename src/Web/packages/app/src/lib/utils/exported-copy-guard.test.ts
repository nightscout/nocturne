import { describe, it, expect } from "vitest";
import { readdirSync, readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { join } from "node:path";
import ts from "typescript";

/**
 * wuchale wraps a top-level declaration in a `.svelte.{js,ts}` module in `$derived` when its
 * initializer holds a string it extracts. Svelte refuses to export derived state, so the module
 * fails to compile under the dev transform and every page that imports it answers 500. Unit tests
 * never run that transform. Copy a module exports goes in a function body, built per call.
 *
 * A string counts when wuchale's default script heuristic would extract it: it has a letter, it
 * starts with an upper-case letter (or, for a template, with a placeholder followed by words), and
 * it is not all capitals. Strings inside a function and in type positions are not wrapped, nor is
 * a node behind a `@wc-ignore` comment or a file marked `@wc-ignore-file`.
 */
const SRC = fileURLToPath(new URL("../..", import.meta.url));

function svelteModules(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) return entry.name === "generated" ? [] : svelteModules(path);
    return /\.svelte\.(js|ts)$/.test(entry.name) && !/\.test\./.test(entry.name) ? [path] : [];
  });
}

function extractable(text: string, startsWithPlaceholder: boolean): boolean {
  if (!/\p{L}/u.test(text)) return false;
  if (/^([A-Z]|\P{L})+$/u.test(text)) return false;
  if (startsWithPlaceholder) return /\s\p{L}/u.test(text);
  return !/[a-z]|\P{L}/u.test(text[0]);
}

function directives(node: ts.Node, file: ts.SourceFile): string[] {
  const trivia = file.text.slice(node.getFullStart(), node.getStart(file));
  return [...trivia.matchAll(/\/\/(.*)|\/\*([\s\S]*?)\*\//g)].map((match) => (match[1] ?? match[2]).trim());
}

function extractedStrings(node: ts.Node, file: ts.SourceFile): string[] {
  if (ts.isFunctionLike(node) || ts.isClassLike(node) || ts.isTypeNode(node)) return [];
  if (directives(node, file).includes("@wc-ignore")) return [];

  if (ts.isStringLiteral(node) || ts.isNoSubstitutionTemplateLiteral(node)) {
    return extractable(node.text, false) ? [node.text] : [];
  }

  if (ts.isTemplateExpression(node)) {
    const text = node.head.text + node.templateSpans.map((span) => `{0}${span.literal.text}`).join("");
    return extractable(text, node.head.text === "") ? [node.getText()] : [];
  }

  const found: string[] = [];
  ts.forEachChild(node, (child) => {
    found.push(...extractedStrings(child, file));
  });
  return found;
}

function offences(source: string, fileName = "module.svelte.ts"): string[] {
  if (/@wc-ignore-file\b/.test(source)) return [];
  const file = ts.createSourceFile(fileName, source, ts.ScriptTarget.Latest, true);

  const exportedNames = new Set(
    file.statements
      .filter(ts.isExportDeclaration)
      .filter((stmt) => !stmt.moduleSpecifier && stmt.exportClause && ts.isNamedExports(stmt.exportClause))
      .flatMap((stmt) => (stmt.exportClause as ts.NamedExports).elements)
      .map((spec) => (spec.propertyName ?? spec.name).text)
  );

  return file.statements.filter(ts.isVariableStatement).flatMap((stmt) => {
    if (directives(stmt, file).includes("@wc-ignore")) return [];
    const exported = stmt.modifiers?.some((m) => m.kind === ts.SyntaxKind.ExportKeyword) ?? false;
    return stmt.declarationList.declarations
      .filter((decl) => exported || (ts.isIdentifier(decl.name) && exportedNames.has(decl.name.text)))
      .flatMap((decl) =>
        decl.initializer
          ? extractedStrings(decl.initializer, file).map((text) => `${decl.name.getText()}: ${text}`)
          : []
      );
  });
}

describe("exported top-level copy in .svelte.{js,ts} modules", () => {
  it("keeps every extracted string out of an exported top-level initializer", () => {
    const modules = svelteModules(SRC);
    expect(modules.some((path) => path.replaceAll("\\", "/").endsWith("lib/forms/submit-messages.svelte.ts"))).toBe(true);

    const found = modules.flatMap((path) =>
      offences(readFileSync(path, "utf8"), path).map((offence) => `${path}: ${offence}`)
    );

    expect(found).toEqual([]);
  });

  it("recognises an exported string the transform would wrap", () => {
    for (const source of [
      `export const MESSAGE = "We couldn't save your changes.";`,
      "export const MESSAGE = `Too many attempts.`;",
      "export const MESSAGE = `Saved ${count} items.`;",
      "export const MESSAGE = `${count} items saved.`;",
      `export let MESSAGE = "Try again.";`,
      `export const LABELS = { info: "Info" };`,
      `export const LABELS = ["Critical", "Warning"] as const;`,
      `export const MESSAGE = ok ? "Saved." : "Not saved.";`,
      `const MESSAGE = "Try again.";\nexport { MESSAGE };`,
      `const MESSAGE = "Try again.";\nexport { MESSAGE as RETRY };`,
    ]) {
      expect(offences(source), source).not.toEqual([]);
    }
  });

  it("leaves strings the transform does not wrap", () => {
    for (const source of [
      `export function message() { return "We couldn't save your changes."; }`,
      `export const message = () => "Try again.";`,
      `export const COOKIE = "nocturne-prefs";`,
      `export const STATES = ["firing", "acknowledged"] as const;`,
      `export const CODE = "OK";`,
      `export const MESSAGE = "";`,
      "export const MESSAGE = `${count}`;",
      `const MESSAGE = "Try again.";`,
      `export const tone: Record<"Info", string> = { info: "text-muted" };`,
      `export class Copy { label = "Save"; }`,
      `// @wc-ignore
export const NAMES = ["HbA1c", "A1c"] as const;`,
      `export const pref = make("key", /* @wc-ignore */ "HbA1c");`,
      `// @wc-ignore-file
export const MESSAGE = "Try again.";`,
    ]) {
      expect(offences(source), source).toEqual([]);
    }
  });
});
