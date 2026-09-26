#!/usr/bin/env node
// Coverage report for a pull request: line and branch coverage per area, the change against
// main's last baseline, and coverage of the lines the pull request adds (patch coverage).
//
//   node .github/scripts/coverage-report.mjs \
//     --reports 'coverage-input/**/*.xml' \
//     --base <sha> \
//     [--baseline baseline/coverage-summary.json] \
//     [--threshold 60] \
//     --out-markdown coverage.md --out-json coverage-summary.json
//
// Reads Cobertura XML only (coverlet and ReportGenerator for .NET, vitest's cobertura reporter
// for the web packages), so every tool lands in one per-file line map: a line's hits are the most
// any report saw, its branches the best covered/total any report saw. Paths are made
// repository-relative from each report's <source> roots.
//
// Exit code: 1 when patch coverage is below --threshold and the patch has coverable lines;
// otherwise 0. Nothing else fails the job: low coverage elsewhere only reports.
// No dependencies beyond Node.

import { execFileSync } from "node:child_process";
import { existsSync, readFileSync, writeFileSync, globSync } from "node:fs";
import { isAbsolute, join, normalize, relative, resolve } from "node:path";

const args = Object.fromEntries(
  process.argv.slice(2).reduce((pairs, arg, i, all) => {
    if (arg.startsWith("--")) pairs.push([arg.slice(2), all[i + 1]?.startsWith("--") ? "true" : all[i + 1]]);
    return pairs;
  }, []),
);

const repo = resolve(args.repo ?? process.cwd());
const threshold = Number(args.threshold ?? process.env.COVERAGE_PATCH_THRESHOLD ?? 60);

/** Areas in report order; the first prefix that matches a file wins. */
export const AREAS = [
  ["API", "src/API/"],
  ["Infrastructure", "src/Infrastructure/"],
  ["Core", "src/Core/"],
  ["Connectors", "src/Connectors/"],
  ["Services", "src/Services/"],
  ["app", "src/Web/packages/app/"],
  ["bot", "src/Web/packages/bot/"],
  ["portal", "src/Web/packages/portal/"],
  ["bridge", "src/Web/packages/bridge/"],
  ["cms", "src/Web/packages/cms/"],
];

export function areaOf(file) {
  for (const [name, prefix] of AREAS) if (file.startsWith(prefix)) return name;
  return null;
}

const attr = (tag, name) => tag.match(new RegExp(`\\b${name}="([^"]*)"`))?.[1];

/**
 * Parses one Cobertura document into Map<repoRelativePath, Map<line, {hits, covered, total}>>.
 * Regex over the XML rather than a DOM: the files run to tens of MB and the shape is fixed.
 */
export function parseCobertura(xml, reportPath) {
  const sources = [...xml.matchAll(/<source>([^<]*)<\/source>/g)].map((m) => m[1].trim()).filter(Boolean);
  const files = new Map();
  const classRe = /<class\b[^>]*>[\s\S]*?<\/class>/g;
  for (const block of xml.match(classRe) ?? []) {
    const open = block.slice(0, block.indexOf(">") + 1);
    const filename = attr(open, "filename");
    if (!filename) continue;
    const path = toRepoPath(filename, sources, reportPath);
    if (!path) continue;
    const lines = files.get(path) ?? new Map();
    const linesPart = block.slice(block.lastIndexOf("<lines>"));
    for (const [tag] of linesPart.matchAll(/<line\b[^>]*\/?>/g)) {
      const number = Number(attr(tag, "number"));
      const hits = Number(attr(tag, "hits") ?? 0);
      const cond = attr(tag, "condition-coverage")?.match(/\((\d+)\/(\d+)\)/);
      mergeLine(lines, number, hits, cond ? Number(cond[1]) : 0, cond ? Number(cond[2]) : 0);
    }
    files.set(path, lines);
  }
  return files;
}

function mergeLine(lines, number, hits, covered, total) {
  const prev = lines.get(number);
  if (!prev) {
    lines.set(number, { hits, covered, total });
    return;
  }
  prev.hits = Math.max(prev.hits, hits);
  if (total > prev.total || (total === prev.total && covered > prev.covered)) {
    prev.covered = covered;
    prev.total = total;
  }
}

function toRepoPath(filename, sources, reportPath) {
  const candidates = isAbsolute(filename) ? [filename] : sources.map((s) => join(s, filename));
  if (!isAbsolute(filename) && sources.length === 0) candidates.push(join(repo, filename));
  for (const candidate of candidates) {
    const rel = relative(repo, normalize(candidate)).replaceAll("\\", "/");
    if (!rel.startsWith("..") && existsSync(join(repo, rel))) return rel;
  }
  // Reports produced on another machine (CI artifacts read locally): match on the repo layout.
  const marker = (isAbsolute(filename) ? filename : candidates[0] ?? filename).replaceAll("\\", "/");
  const at = marker.indexOf("/src/");
  if (at >= 0) {
    const rel = marker.slice(at + 1);
    if (existsSync(join(repo, rel))) return rel;
  }
  process.stderr.write(`coverage-report: ${reportPath}: cannot place ${filename}\n`);
  return null;
}

export function mergeReports(parsed) {
  const all = new Map();
  for (const files of parsed) {
    for (const [path, lines] of files) {
      const into = all.get(path) ?? new Map();
      for (const [n, l] of lines) mergeLine(into, n, l.hits, l.covered, l.total);
      all.set(path, into);
    }
  }
  return all;
}

export function summarise(files) {
  const areas = Object.fromEntries(AREAS.map(([name]) => [name, { lines: 0, coveredLines: 0, branches: 0, coveredBranches: 0 }]));
  for (const [path, lines] of files) {
    const area = areaOf(path);
    if (!area) continue;
    const a = areas[area];
    for (const l of lines.values()) {
      a.lines++;
      if (l.hits > 0) a.coveredLines++;
      a.branches += l.total;
      a.coveredBranches += l.covered;
    }
  }
  return areas;
}

/** Lines each file gains in HEAD relative to `base`: Map<path, Set<line>>. */
export function addedLines(base) {
  const diff = execFileSync("git", ["diff", "--unified=0", "--no-color", "--no-renames", `${base}...HEAD`], {
    cwd: repo,
    encoding: "utf8",
    maxBuffer: 256 * 1024 * 1024,
  });
  const added = new Map();
  let file = null;
  for (const line of diff.split("\n")) {
    if (line.startsWith("+++ ")) {
      file = line === "+++ /dev/null" ? null : line.slice(6);
      continue;
    }
    const hunk = line.match(/^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@/);
    if (hunk && file) {
      const start = Number(hunk[1]);
      const count = hunk[2] === undefined ? 1 : Number(hunk[2]);
      const set = added.get(file) ?? new Set();
      for (let n = start; n < start + count; n++) set.add(n);
      added.set(file, set);
    }
  }
  return added;
}

export function patchCoverage(files, added) {
  let coverable = 0;
  let covered = 0;
  const perFile = [];
  for (const [path, lineNumbers] of added) {
    const lines = files.get(path);
    if (!lines) continue;
    let fileCoverable = 0;
    let fileCovered = 0;
    const missed = [];
    for (const n of lineNumbers) {
      const l = lines.get(n);
      if (!l) continue;
      fileCoverable++;
      if (l.hits > 0) fileCovered++;
      else missed.push(n);
    }
    if (fileCoverable === 0) continue;
    coverable += fileCoverable;
    covered += fileCovered;
    perFile.push({ path, coverable: fileCoverable, covered: fileCovered, missed });
  }
  perFile.sort((a, b) => b.coverable - b.covered - (a.coverable - a.covered));
  return { coverable, covered, perFile };
}

const pct = (covered, total) => (total === 0 ? null : (100 * covered) / total);
const fmt = (value) => (value === null ? "–" : `${value.toFixed(1)}%`);
const delta = (now, before) => {
  if (now === null || before === null || before === undefined) return "";
  const d = now - before;
  if (Math.abs(d) < 0.05) return " (±0)";
  return ` (${d > 0 ? "+" : ""}${d.toFixed(1)})`;
};

function ranges(numbers) {
  const sorted = [...numbers].sort((a, b) => a - b);
  const out = [];
  for (let i = 0; i < sorted.length; i++) {
    let j = i;
    while (j + 1 < sorted.length && sorted[j + 1] === sorted[j] + 1) j++;
    out.push(i === j ? `${sorted[i]}` : `${sorted[i]}-${sorted[j]}`);
    i = j;
  }
  return out;
}

export function renderMarkdown({ areas, baseline, patch, threshold, meta }) {
  const out = ["<!-- nocturne-coverage -->", "## Coverage", ""];
  if (patch) {
    const p = pct(patch.covered, patch.coverable);
    const verdict =
      p === null ? "no coverable lines changed" : p >= threshold ? `meets the ${threshold}% gate` : `**below the ${threshold}% gate**`;
    out.push(`**Patch coverage: ${fmt(p)}** of ${patch.coverable} changed coverable lines (${verdict}).`, "");
  }
  out.push("| Area | Lines | Branches |", "|---|---|---|");
  for (const [name] of AREAS) {
    const a = areas[name];
    if (a.lines === 0) continue;
    const b = baseline?.areas?.[name];
    const lp = pct(a.coveredLines, a.lines);
    const bp = pct(a.coveredBranches, a.branches);
    const lb = b ? pct(b.coveredLines, b.lines) : undefined;
    const bb = b ? pct(b.coveredBranches, b.branches) : undefined;
    out.push(`| ${name} | ${fmt(lp)}${delta(lp, lb)} | ${fmt(bp)}${delta(bp, bb)} |`);
  }
  out.push("");
  out.push(baseline ? `Changes in parentheses are percentage points against main at \`${baseline.commit?.slice(0, 7) ?? "?"}\`.` : "No main baseline was available, so no change against main is shown.");
  if (patch && patch.perFile.some((f) => f.missed.length > 0)) {
    out.push("", "<details><summary>Changed lines not covered</summary>", "", "| File | Covered | Lines not covered |", "|---|---|---|");
    for (const f of patch.perFile.filter((f) => f.missed.length > 0).slice(0, 40)) {
      const lines = ranges(f.missed);
      out.push(`| \`${f.path}\` | ${f.covered}/${f.coverable} | ${lines.slice(0, 12).join(", ")}${lines.length > 12 ? ", …" : ""} |`);
    }
    out.push("", "</details>");
  }
  if (meta) out.push("", `<sub>${meta}</sub>`);
  return out.join("\n") + "\n";
}

function main() {
  const patterns = (args.reports ?? "").split(",").filter(Boolean);
  const reportFiles = [...new Set(patterns.flatMap((p) => globSync(p, { cwd: repo }).map((f) => resolve(repo, f))))];
  if (reportFiles.length === 0) {
    console.error("coverage-report: no Cobertura reports matched");
    process.exit(2);
  }
  const files = mergeReports(reportFiles.map((f) => parseCobertura(readFileSync(f, "utf8"), f)));
  const areas = summarise(files);
  const baseline = args.baseline && existsSync(args.baseline) ? JSON.parse(readFileSync(args.baseline, "utf8")) : null;
  const patch = args.base ? patchCoverage(files, addedLines(args.base)) : null;

  const commit = execFileSync("git", ["rev-parse", "HEAD"], { cwd: repo, encoding: "utf8" }).trim();
  const markdown = renderMarkdown({
    areas,
    baseline,
    patch,
    threshold,
    meta: `${reportFiles.length} reports, commit ${commit.slice(0, 7)}. Gate: patch coverage ≥ ${threshold}% (COVERAGE_PATCH_THRESHOLD).`,
  });
  if (args["out-markdown"]) writeFileSync(args["out-markdown"], markdown);
  if (args["out-json"]) {
    writeFileSync(args["out-json"], JSON.stringify({ commit, areas, patch: patch && { coverable: patch.coverable, covered: patch.covered } }, null, 2));
  }
  process.stdout.write(markdown);

  const p = patch ? pct(patch.covered, patch.coverable) : null;
  if (p !== null && p < threshold) {
    console.error(`::error::Patch coverage ${p.toFixed(1)}% is below the ${threshold}% gate`);
    process.exit(1);
  }
}

if (import.meta.url === `file://${process.argv[1]}`) main();
