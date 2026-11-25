// Merges a translator's pull request that changes only the locale file they are allowlisted for in
// .github/translators.json. Called by translator-merge.yml after Tests passes on the pull request.
//
// That workflow holds a write token, so nothing here runs or checks out the pull request: its file
// list, author and statuses come from the API, and its catalog is fetched as text and only parsed.

const MARKER = "<!-- nocturne-translator-merge -->";
const CLA_CONTEXT = "license/cla";
const MAX_LISTED_PROBLEMS = 20;

// Wuchale's interpolation holes ({0}) and markup slots (<0>, </0>, <0/>). Plural `#` is left out: a
// language may write the singular as a word.
const HOLE = /\{\d+\}/g;
const SLOT = /<\/?\d+\/?>/g;

function unquote(line, lineNumber) {
  const match = line.match(/^"((?:[^"\\]|\\.)*)"\s*$/);
  if (!match) throw new Error(`line ${lineNumber}: expected a quoted string`);
  return match[1].replace(/\\(.)/g, (_, c) => ({ n: "\n", t: "\t", r: "\r" })[c] ?? c);
}

/**
 * Parses a gettext catalog into a map keyed by context and msgid. Obsolete (`#~`) entries and the
 * header are left out. Throws on a line it cannot read, so a malformed catalog never merges.
 */
function parsePo(text) {
  const entries = new Map();
  let entry = null;
  let field = null;

  const finish = () => {
    if (entry && entry.msgid !== undefined && entry.msgid !== "") {
      entries.set(`${entry.msgctxt ?? ""}\u0004${entry.msgid}`, entry);
    }
    entry = null;
    field = null;
  };

  const lines = text.split(/\r?\n/);
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i].trim();
    const lineNumber = i + 1;
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
      if (!field) throw new Error(`line ${lineNumber}: a string continues no field`);
      append(entry, field, unquote(line, lineNumber));
      continue;
    }
    const keyword = line.match(/^(msgctxt|msgid_plural|msgid|msgstr(?:\[(\d+)\])?)\s+(".*)$/);
    if (!keyword) throw new Error(`line ${lineNumber}: not a catalog line`);
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
  return entries;
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
 * their source text (see mismatch). An empty translation is untranslated, not a problem.
 */
function placeholderProblems(baseText, headText) {
  const base = parsePo(baseText);
  const head = parsePo(headText);
  const problems = [];

  for (const [key, entry] of head) {
    const before = base.get(key);
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
  const fence = clipped.includes("`") ? "``" : "`";
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
  const { data } = await github.rest.repos.getContent({
    owner,
    repo,
    path,
    ref,
    mediaType: { format: "raw" },
  });
  return typeof data === "string" ? data : Buffer.from(data).toString("utf8");
}

/** The locale files a login may merge alone. Keys starting with `$` are not files. */
function filesFor(allowlist, login) {
  const lower = login.toLowerCase();
  return Object.entries(allowlist)
    .filter(([file, logins]) => !file.startsWith("$") && logins.some((l) => l.toLowerCase() === lower))
    .map(([file]) => file);
}

async function mergeIfEligible({ github, context, core, pr, allowlist, headSha }) {
  const { owner, repo } = context.repo;
  const prNumber = pr.number;
  const comment = (body) => upsertComment({ github, owner, repo, prNumber, body });

  if (pr.draft) return core.info(`#${prNumber} is a draft`);

  const allowed = filesFor(allowlist, pr.user.login);
  if (allowed.length === 0) return core.info(`#${prNumber}: ${pr.user.login} is not an allowlisted translator`);

  const files = await github.paginate(github.rest.pulls.listFiles, { owner, repo, pull_number: prNumber, per_page: 100 });
  const [only] = files;
  if (files.length !== 1 || only.status !== "modified" || !allowed.includes(only.filename)) {
    const list = allowed.map((f) => `\`${f}\``).join(", ");
    return comment(`Not merged automatically: this pull request changes files other than ${list}. A maintainer will review it.`);
  }

  const { data: status } = await github.rest.repos.getCombinedStatusForRef({ owner, repo, ref: headSha });
  const cla = status.statuses.find((s) => s.context === CLA_CONTEXT);
  if (cla?.state !== "success") {
    return comment(
      "Not merged automatically: the Contributor License Agreement is not signed yet. Sign it, then push a commit or ask a maintainer to run the tests again.",
    );
  }

  const [baseText, headText] = await Promise.all([
    fileText({ github, owner, repo, path: only.filename, ref: pr.base.ref }),
    fileText({ github, owner: pr.head.repo.owner.login, repo: pr.head.repo.name, path: only.filename, ref: headSha }),
  ]);

  let problems;
  try {
    problems = placeholderProblems(baseText, headText);
  } catch (error) {
    return comment(`Not merged automatically: \`${only.filename}\` cannot be read as a catalog (${error.message}).`);
  }
  if (problems.length > 0) return comment(problemsMarkdown(only.filename, problems));

  try {
    await github.rest.pulls.merge({ owner, repo, pull_number: prNumber, sha: headSha, merge_method: "squash" });
  } catch (error) {
    core.warning(`#${prNumber}: merge failed: ${error.message}`);
    return comment(`Not merged automatically: GitHub refused the merge (${error.message}). Update the branch from \`main\`, then push.`);
  }
  await comment(
    `Merged automatically: \`${pr.user.login}\` is the allowlisted translator for \`${only.filename}\`, the tests passed, and the placeholders match the English text.`,
  );
  return true;
}

/**
 * Finds the open pull requests into the default branch whose head is the commit Tests passed on,
 * and merges those an allowlisted translator opened. Returns whether anything merged.
 */
async function run({ github, context, core, allowlist, headSha }) {
  const { owner, repo } = context.repo;
  const defaultBranch = context.payload.repository.default_branch;
  const open = await github.paginate(github.rest.pulls.list, { owner, repo, state: "open", base: defaultBranch, per_page: 100 });
  const prs = open.filter((pr) => pr.head.sha === headSha);
  if (prs.length === 0) {
    core.info(`no open pull request has head ${headSha}`);
    return false;
  }
  let merged = false;
  for (const pr of prs) {
    if ((await mergeIfEligible({ github, context, core, pr, allowlist, headSha })) === true) merged = true;
  }
  return merged;
}

module.exports = { run, parsePo, placeholderProblems, filesFor };
