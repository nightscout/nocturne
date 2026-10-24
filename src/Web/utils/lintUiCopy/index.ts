#!/usr/bin/env node --experimental-strip-types

// Lints every user-facing source string against the Vale rules in
// .vale/styles/Nocturne. Vale cannot parse .svelte or .po, so the strings that
// wuchale extracted into locales/en.po go to a .uicopy file, one per paragraph,
// and each alert maps back to the components that use the string.
//
//   pnpm run lint:copy                       every alert, grouped by rule
//   pnpm run lint:copy --min=error           blocking alerts only
//   pnpm run lint:copy --rule=Nocturne.Terms one rule
//   pnpm run lint:copy --json                machine-readable

import { spawnSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, "../../../..");
const catalogPath = path.resolve(here, "../../locales/en.po");
const exceptionsPath = path.join(here, "exceptions.txt");

const levels = ["suggestion", "warning", "error"] as const;
type Level = (typeof levels)[number];

interface Entry {
  msgid: string;
  references: string[];
}

interface ValeAlert {
  Check: string;
  Line: number;
  Match: string;
  Message: string;
  Severity: Level;
}

interface Finding {
  rule: string;
  severity: Level;
  message: string;
  match: string;
  msgid: string;
  references: string[];
}

const args = process.argv.slice(2);
const flag = (name: string) =>
  args.find((a) => a.startsWith(`--${name}=`))?.split("=")[1];
const minLevel = (flag("min") ?? "suggestion") as Level;
const onlyRule = flag("rule");
const asJson = args.includes("--json");

function unquote(line: string): string {
  return JSON.parse(line.slice(line.indexOf('"')));
}

/** Reads msgid and msgid_plural strings with their `#:` source references. */
function readCatalog(file: string): Entry[] {
  const entries: Entry[] = [];
  let references: string[] = [];
  let current: string[] | null = null;
  const flush = () => {
    if (current === null) return;
    const text = current.join("");
    if (text !== "") entries.push({ msgid: text, references });
    current = null;
  };

  for (const raw of fs.readFileSync(file, "utf8").split(/\r?\n/)) {
    const line = raw.trim();
    if (line.startsWith("#:")) {
      references.push(line.slice(2).trim());
    } else if (line.startsWith("msgid ") || line.startsWith("msgid_plural ")) {
      flush();
      current = [unquote(line)];
    } else if (line.startsWith('"') && current !== null) {
      current.push(unquote(line));
    } else if (line.startsWith("msgstr")) {
      flush();
      references = [];
    } else if (line === "") {
      flush();
      references = [];
    }
  }
  flush();
  return entries;
}

/**
 * Wuchale writes `{0}` for an interpolation and `<0>`, `</0>`, `<0/>` for a
 * nested element. A placeholder or a self-closing element (a name, an icon)
 * reads as one word, the way rule 8.6 counts an identifier. A tag that wraps
 * text carries no words of its own.
 */
function toProse(msgid: string): string {
  return msgid
    .replace(/<\d+\/>/g, "X")
    .replace(/<\/?\d+>/g, "")
    .replace(/\{\d+\}/g, "X")
    .replace(/\s+/g, " ")
    .trim();
}

/**
 * STE strips persuasion by design, so the portal's marketing pages (home,
 * features, blog, roadmap) are out of scope. Its docs are in scope. A string
 * is linted when any component that uses it is in scope.
 */
function inScope(reference: string): boolean {
  if (!reference.startsWith("../portal/")) return true;
  return reference.includes("/docs/") || /\/Docs\w*\.svelte$/.test(reference);
}

/** One `rule<TAB>msgid` per line. A listed pair is a deliberate exception. */
function readExceptions(): Set<string> {
  if (!fs.existsSync(exceptionsPath)) return new Set();
  return new Set(
    fs
      .readFileSync(exceptionsPath, "utf8")
      .split(/\r?\n/)
      .filter((l) => l.trim() !== "" && !l.startsWith("#")),
  );
}

const entries = readCatalog(catalogPath);
const unique = [
  ...new Map(
    entries
      .filter((e) => e.references.some(inScope))
      .map((e) => [e.msgid, e]),
  ).values(),
];

const workDir = fs.mkdtempSync(path.join(os.tmpdir(), "nocturne-ui-copy-"));
const copyFile = path.join(workDir, "strings.uicopy");
const lineToEntry: Entry[] = [];
const lines: string[] = [];
for (const entry of unique) {
  const prose = toProse(entry.msgid);
  if (prose === "") continue;
  lineToEntry[lines.length + 1] = entry;
  lines.push(prose, "");
}
fs.writeFileSync(copyFile, lines.join("\n"));

const vale = spawnSync(
  "vale",
  [
    `--config=${path.join(repoRoot, ".vale.ini")}`,
    `--minAlertLevel=${minLevel}`,
    "--output=JSON",
    copyFile,
  ],
  { encoding: "utf8", maxBuffer: 256 * 1024 * 1024 },
);
fs.rmSync(workDir, { recursive: true, force: true });

if (vale.error) {
  console.error(
    "vale is not installed. See https://vale.sh/docs/install (winget install errata-ai.Vale).",
  );
  process.exit(2);
}
if (vale.status !== 0 && vale.status !== 1) {
  console.error(vale.stderr || vale.stdout);
  process.exit(2);
}

const report = JSON.parse(vale.stdout || "{}") as Record<string, ValeAlert[]>;
const exceptions = readExceptions();
const findings: Finding[] = Object.values(report)
  .flat()
  .filter((a) => !onlyRule || a.Check === onlyRule)
  .map((a) => {
    const entry = lineToEntry[a.Line];
    return {
      rule: a.Check,
      severity: a.Severity,
      message: a.Message,
      match: a.Match,
      msgid: entry.msgid,
      references: [...new Set(entry.references)],
    };
  })
  .filter((f) => !exceptions.has(`${f.rule}\t${f.msgid}`));

if (asJson) {
  console.log(JSON.stringify(findings, null, 2));
} else {
  const byRule = Map.groupBy(findings, (f) => f.rule);
  for (const [rule, group] of [...byRule].sort((a, b) =>
    a[0].localeCompare(b[0]),
  )) {
    console.log(`\n${rule} (${group[0].severity}, ${group.length})`);
    for (const f of group) {
      console.log(`  "${f.msgid}"`);
      console.log(`    ${f.message}`);
      for (const ref of f.references) console.log(`    ${ref}`);
    }
  }
  const counts = levels
    .map((l) => `${findings.filter((f) => f.severity === l).length} ${l}s`)
    .reverse()
    .join(", ");
  console.log(`\n${unique.length} strings checked: ${counts}.`);
}

process.exit(findings.some((f) => f.severity === "error") ? 1 : 0);
