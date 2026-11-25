// Merges a translator's pull request that changes only the locale catalog they are allowlisted for
// in .github/translators.json. Called by translator-merge.yml after Tests passes on the pull request.
//
// That workflow holds a write token, so nothing here runs or checks out the pull request. Every check
// reads the tested head commit by its sha, never the pull request's live file list or head, and the
// merge is pinned to that sha: otherwise a force-push between the check and the merge could swap in
// other files.

const MARKER = "<!-- nocturne-translator-merge -->";
const CLA_CONTEXT = "license/cla";
const MAX_LISTED_PROBLEMS = 20;

// Wuchale's interpolation holes ({0}) and markup slots (<0>, </0>, <0/>). Plural `#` is left out: a
// language may write the singular as a word.
const HOLE = /\{\d+\}/g;
const SLOT = /<\/?\d+\/?>/g;

// Wuchale reads catalogs with pofile, which also decodes \x and octal escapes. A catalog never needs
// them, and decoding them differently here would let `\x3c0/>` pass as text and render as a slot,
// so any other escape is refused.
const ESCAPES = { n: "\n", t: "\t", r: "\r", '"': '"', "\\": "\\" };

class CatalogError extends Error {}

function unquote(line, lineNumber) {
  const match = line.match(/^"((?:[^"\\]|\\.)*)"\s*$/);
  if (!match) throw new CatalogError(`line ${lineNumber}: expected a quoted string`);
  return match[1].replace(/\\(.)/g, (_, c) => {
    if (!(c in ESCAPES)) throw new CatalogError(`line ${lineNumber}: unsupported escape \\${c}`);
    return ESCAPES[c];
  });
}

/**
 * Parses a gettext catalog the way Wuchale loads it: obsolete (`#~`) entries count, since Wuchale
 * decides what is obsolete itself, and a later entry with the same context and msgid replaces an
 * earlier one. Returns the header (the `msgid ""` translation) and the entries keyed by context and
 * msgid. Throws a CatalogError on a line it cannot read, so a malformed catalog never merges.
 */
function parsePo(text) {
  const entries = new Map();
  let header = null;
  let entry = null;
  let field = null;

  const finish = () => {
    if (entry && entry.msgid !== undefined) {
      if (entry.msgid === "" && entry.msgctxt === undefined) header = entry.msgstr.join("\u0000");
      else entries.set(`${entry.msgctxt ?? ""}\u0004${entry.msgid}`, entry);
    }
    entry = null;
    field = null;
  };

  const lines = text.replace(/^﻿/, "").split(/\r?\n/);
  for (let i = 0; i < lines.length; i++) {
    let line = lines[i].trim();
    const lineNumber = i + 1;
    if (line.startsWith("#~") && !line.startsWith("#~|")) line = line.slice(2).trim();
    if (line === "") {
      finish();
      continue;
    }
    if (line.startsWith("#")) {
      // A comment after a string field starts the next entry.
      if (field) finish();
      continue;
    }
    if (line.startsWith('"')) {
      if (!field) throw new CatalogError(`line ${lineNumber}: a string continues no field`);
      append(entry, field, unquote(line, lineNumber));
      continue;
    }
    const keyword = line.match(/^(msgctxt|msgid_plural|msgid|msgstr(?:\[(\d+)\])?)\s+(".*)$/);
    if (!keyword) throw new CatalogError(`line ${lineNumber}: not a catalog line`);
    const [, name, index, rest] = keyword;
    // msgctxt opens an entry, and so does a msgid that no msgctxt opened.
    if (field && (name === "msgctxt" || (name === "msgid" && field.name !== "msgctxt"))) finish();
    entry ??= { msgstr: [] };
    field = index !== undefined ? { name: "msgstr", index: Number(index) } : name === "msgstr" ? { name, index: 0 } : { name };
    if (field.name === "msgstr") entry.msgstr[field.index] = "";
    else entry[field.name] = "";
    append(entry, field, unquote(rest, lineNumber));
  }
  finish();
  return { header, entries };
}

/** Every Plural-Forms line of a header, in order. */
function pluralForms(header) {
  return (header ?? "")
    .split("\n")
    .filter((line) => /^\s*plural-forms\s*:/i.test(line))
    .join("\n");
}

function append(entry, field, text) {
  if (field.name === "msgstr") entry.msgstr[field.index] += text;
  else entry[field.name] += text;
}

function tokens(text, pattern) {
  return [...new Set(text.match(pattern) ?? [])].sort();
}

/**
 * Why a translation cannot stand for its source, or null when it can. Every markup slot must be
 * kept, since a missing or extra one drops or misplaces a link or icon, and is the usual sign of a
 * translation shifted onto the wrong entry. A hole may be left out or repeated, as when English
 * spells a plural ending as one ("{0} reading{1}"), but not invented.
 */
function mismatch(sources, translation) {
  const slots = tokens(translation, SLOT).join(" ");
  if (!sources.some((source) => tokens(source, SLOT).join(" ") === slots)) {
    return { expected: tokens(sources[0], SLOT).join(" ") || "none", found: slots || "none" };
  }
  const known = new Set(sources.flatMap((source) => tokens(source, HOLE)));
  const invented = tokens(translation, HOLE).filter((hole) => !known.has(hole));
  if (invented.length > 0) {
    return { expected: [...known].sort().join(" ") || "none", found: invented.join(" ") };
  }
  return null;
}

/**
 * The translations the head catalog adds or changes against the base catalog that cannot stand for
 * their source text (see mismatch). Both are parsed catalogs. An empty translation is untranslated,
 * not a problem.
 */
function placeholderProblems(base, head) {
  const problems = [];
  for (const [key, entry] of head.entries) {
    const before = base.entries.get(key);
    if (before && before.msgstr.join("\u0000") === entry.msgstr.join("\u0000")) continue;

    const sources = entry.msgid_plural === undefined ? [entry.msgid] : [entry.msgid, entry.msgid_plural];
    entry.msgstr.forEach((translation, index) => {
      const problem = translation === "" ? null : mismatch(sources, translation);
      if (!problem) return;
      problems.push({
        msgid: entry.msgid,
        msgstr: translation,
        index: entry.msgid_plural !== undefined ? index : undefined,
        ...problem,
      });
    });
  }
  return problems;
}

function codeSpan(text) {
  const flat = text.replace(/\s+/g, " ").replace(/\|/g, "\\|");
  const clipped = flat.length > 120 ? `${flat.slice(0, 117)}...` : flat;
  const longestRun = Math.max(0, ...(clipped.match(/`+/g) ?? []).map((run) => run.length));
  const fence = "`".repeat(longestRun + 1);
  return `${fence} ${clipped} ${fence}`;
}

function problemsMarkdown(file, problems) {
  const rows = problems.slice(0, MAX_LISTED_PROBLEMS).map((p) => {
    const source = p.index === undefined ? codeSpan(p.msgid) : `${codeSpan(p.msgid)} (form ${p.index})`;
    return `| ${source} | ${codeSpan(p.msgstr)} | ${codeSpan(p.expected)} | ${codeSpan(p.found)} |`;
  });
  const more = problems.length > MAX_LISTED_PROBLEMS ? `\n\nAnd ${problems.length - MAX_LISTED_PROBLEMS} more.` : "";
  return [
    `Not merged automatically: ${problems.length} changed ${problems.length === 1 ? "translation has" : "translations have"} different placeholders from the English text in \`${file}\`.`,
    "",
    "A translation must keep every `<0>`, `</0>` and `<0/>` of the English text and add none. It can leave out or repeat a `{0}`, but it must not use one the English text does not have. The order can change.",
    "",
    "| English | Translation | Expected | Found |",
    "|---|---|---|---|",
    ...rows,
  ].join("\n") + more;
}

async function upsertComment({ github, owner, repo, prNumber, body }) {
  body = `${MARKER}\n${body}`;
  const comments = await github.paginate(github.rest.issues.listComments, {
    owner,
    repo,
    issue_number: prNumber,
    per_page: 100,
  });
  const existing = comments.find((c) => c.user?.type === "Bot" && c.body?.startsWith(MARKER));
  if (existing) {
    if (existing.body !== body) await github.rest.issues.updateComment({ owner, repo, comment_id: existing.id, body });
  } else {
    await github.rest.issues.createComment({ owner, repo, issue_number: prNumber, body });
  }
}

async function fileText({ github, owner, repo, path, ref }) {
  const { data } = await github.rest.repos.getContent({ owner, repo, path, ref, mediaType: { format: "raw" } });
  return typeof data === "string" ? data : Buffer.from(data).toString("utf8");
}

/**
 * The locale files a GitHub account may merge alone. Matched by account id, since a login freed by
 * a rename can be registered by someone else. Keys starting with `$` are not files.
 */
function filesFor(allowlist, userId) {
  return Object.entries(allowlist)
    .filter(([file, translators]) => !file.startsWith("$") && translators.some((t) => t.id === userId))
    .map(([file]) => file);
}

/**
 * The English strings whose translation the head catalog adds or changes against the merge base and
 * that the current catalog no longer has as they were: removed, or with another plural source. The
 * squash applies the pull request's edits to the current catalog line by line, so such a translation
 * could land on another string than the one it was checked against.
 */
function movedStrings(mergeBase, head, current) {
  const moved = [];
  for (const [key, entry] of head.entries) {
    const before = mergeBase.entries.get(key);
    if (before && before.msgstr.join("\u0000") === entry.msgstr.join("\u0000")) continue;
    const now = current.entries.get(key);
    if (!now || now.msgid_plural !== entry.msgid_plural) moved.push(entry.msgid);
  }
  return moved;
}

async function fileObject({ github, owner, repo, path, ref }) {
  const { data } = await github.rest.repos.getContent({ owner, repo, path, ref, mediaType: { format: "object" } });
  return data;
}

async function mergeIfEligible({ github, context, core, pr, allowlist, headSha }) {
  const { owner, repo } = context.repo;
  const defaultBranch = context.payload.repository.default_branch;
  const prNumber = pr.number;
  const comment = (body) => upsertComment({ github, owner, repo, prNumber, body });

  if (pr.draft) return core.info(`#${prNumber} is a draft`);

  const allowed = filesFor(allowlist, pr.user.id);
  if (allowed.length === 0) return core.info(`#${prNumber}: ${pr.user.login} is not an allowlisted translator`);
  const allowedList = allowed.map((f) => `\`${f}\``).join(", ");

  const [{ data: live }, { data: branch }] = await Promise.all([
    github.rest.pulls.get({ owner, repo, pull_number: prNumber }),
    github.rest.git.getRef({ owner, repo, ref: `heads/${defaultBranch}` }),
  ]);
  if (live.state !== "open" || live.head.sha !== headSha) return core.info(`#${prNumber} has moved on from ${headSha}`);
  if (live.mergeable === false) {
    return comment(`Not merged automatically: this pull request conflicts with \`${defaultBranch}\`. Update the branch, then push.`);
  }
  const mainSha = branch.object.sha;

  // Three dots: what the tested head changes since it left the default branch, which is what the
  // squash brings in.
  const { data: diff } = await github.rest.repos.compareCommitsWithBasehead({
    owner,
    repo,
    basehead: `${mainSha}...${headSha}`,
    per_page: 100,
  });
  const files = diff.files ?? [];
  const [only] = files;
  if (files.length !== 1 || only.status !== "modified" || !allowed.includes(only.filename)) {
    return comment(
      `Not merged automatically: this pull request must change only ${allowed.length === 1 ? "" : "one of "}${allowedList}, and must not add, rename or delete it. A maintainer will review it.`,
    );
  }
  const path = only.filename;
  const mergeBaseSha = diff.merge_base_commit.sha;

  const [headObject, mergeBaseObject, mainObject] = await Promise.all(
    [headSha, mergeBaseSha, mainSha].map((ref) => fileObject({ github, owner, repo, path, ref })),
  );
  if (headObject.type !== "file") {
    return comment(`Not merged automatically: \`${path}\` must stay a regular file. A maintainer will review it.`);
  }

  const { data: status } = await github.rest.repos.getCombinedStatusForRef({ owner, repo, ref: headSha });
  const cla = status.statuses.find((s) => s.context === CLA_CONTEXT);
  if (cla?.state !== "success") {
    return comment(
      "Not merged automatically: the Contributor License Agreement is not signed yet. Sign it, then push a commit or ask a maintainer to run the tests again.",
    );
  }

  const mainChanged = mainObject.sha !== mergeBaseObject.sha;
  const [mergeBaseText, headText, mainText] = await Promise.all([
    fileText({ github, owner, repo, path, ref: mergeBaseSha }),
    fileText({ github, owner, repo, path, ref: headSha }),
    mainChanged ? fileText({ github, owner, repo, path, ref: mainSha }) : null,
  ]);

  let mergeBase;
  let current;
  try {
    mergeBase = parsePo(mergeBaseText);
    current = mainChanged ? parsePo(mainText) : mergeBase;
  } catch (error) {
    if (!(error instanceof CatalogError)) throw error;
    core.warning(`${path} on ${defaultBranch} cannot be read: ${error.message}`);
    return comment(`Not merged automatically: \`${path}\` on \`${defaultBranch}\` cannot be read, so this change cannot be checked. A maintainer will review it.`);
  }
  let head;
  try {
    head = parsePo(headText);
  } catch (error) {
    if (!(error instanceof CatalogError)) throw error;
    return comment(`Not merged automatically: \`${path}\` cannot be read as a catalog (${error.message}).`);
  }

  // Wuchale compiles the header's Plural-Forms rule into code. The other header lines are metadata
  // that translators' editors rewrite.
  if (pluralForms(head.header) !== pluralForms(mergeBase.header)) {
    return comment(`Not merged automatically: this pull request changes the \`Plural-Forms\` line of \`${path}\`. A maintainer will review it.`);
  }

  const problems = placeholderProblems(mergeBase, head);
  if (problems.length > 0) return comment(problemsMarkdown(path, problems));

  const moved = mainChanged ? movedStrings(mergeBase, head, current) : [];
  if (moved.length > 0) {
    return comment(
      `Not merged automatically: \`${defaultBranch}\` has changed or removed ${moved.length} of the English strings this pull request translates. Update the branch from \`${defaultBranch}\`, then push.`,
    );
  }

  try {
    await github.rest.pulls.merge({
      owner,
      repo,
      pull_number: prNumber,
      sha: headSha,
      merge_method: "squash",
      commit_title: `${pr.title} (#${prNumber})`,
      commit_message: `Merged by translator-merge: ${pr.user.login} is the allowlisted translator for ${path}.`,
    });
  } catch (error) {
    core.warning(`#${prNumber}: merge failed: ${error.message}`);
    return comment(`Not merged automatically: GitHub refused the merge (${error.message}). Update the branch from \`${defaultBranch}\`, then push.`);
  }
  try {
    await comment(
      `Merged automatically: \`${pr.user.login}\` is the allowlisted translator for \`${path}\`, the tests passed, and the placeholders match the English text.`,
    );
  } catch (error) {
    core.warning(`#${prNumber}: merged, but the comment failed: ${error.message}`);
  }
  return true;
}

/**
 * Finds the open pull requests into the default branch whose head is the commit and repository
 * Tests passed on, and merges those an allowlisted translator opened. Returns whether anything
 * merged.
 */
async function run({ github, context, core, allowlist, headSha, headRepository }) {
  const { owner, repo } = context.repo;
  const defaultBranch = context.payload.repository.default_branch;
  const open = await github.paginate(github.rest.pulls.list, { owner, repo, state: "open", base: defaultBranch, per_page: 100 });
  const prs = open.filter((pr) => pr.head.sha === headSha && pr.head.repo?.full_name === headRepository);
  if (prs.length === 0) {
    core.info(`no open pull request from ${headRepository} has head ${headSha}`);
    return false;
  }
  let merged = false;
  for (const pr of prs) {
    if ((await mergeIfEligible({ github, context, core, pr, allowlist, headSha })) === true) merged = true;
  }
  return merged;
}

module.exports = { run, parsePo, placeholderProblems, pluralForms, movedStrings, filesFor, CatalogError };
