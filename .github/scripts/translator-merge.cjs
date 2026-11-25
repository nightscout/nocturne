// Lands a translator's pull request that changes only the locale catalog they are allowlisted for in
// .github/translators.json. Called by translator-merge.yml after Tests passes on the pull request.
//
// That workflow holds a write token, so nothing here runs or checks out the pull request. It reads
// the tested head commit by its sha, never the pull request's live head, and it does not let GitHub
// merge the branch: a branch's history can make a merge change files its diff does not show (two
// merge bases). It writes one commit on the default branch instead, whose tree is the default
// branch's with only the checked catalog replaced, and closes the pull request.
//
// Catalogs are read and written with the pofile Wuchale uses (vendor/pofile, kept identical to the
// installed copy by the web-static job), so the checks see what the app will see.

const PO = require("./vendor/pofile/po.js");

const MARKER = "<!-- nocturne-translator-merge -->";
const CLA_CONTEXT = "license/cla";
const MAX_LISTED_PROBLEMS = 20;
const LAND_ATTEMPTS = 3;
// The Wuchale whose loading rules itemKey and readCatalog copy; web-static fails when another is
// installed, so they are checked again on an upgrade.
const WUCHALE_VERSION = "0.25.8";

// Wuchale's interpolation holes ({0}) and markup slots (<0>, </0>, <0/>). Plural `#` is left out: a
// language may write the singular as a word.
const HOLE = /\{\d+\}/g;
const SLOT = /<\/?\d+\/?>/g;

/** Wuchale's key for a catalog item (getKey in wuchale/dist/adapters.js). */
function itemKey(item) {
  const id = item.msgid_plural ? [item.msgid, item.msgid_plural] : [item.msgid];
  return `${id.join("\n")}\n${item.msgctxt ?? ""}`.trim();
}

/**
 * A catalog as Wuchale loads it: the Plural-Forms header, and the items by key, a later duplicate
 * replacing an earlier one. Obsolete items count, since Wuchale decides what is obsolete itself.
 */
function readCatalog(text) {
  const po = PO.parse(text);
  const items = new Map();
  for (const item of po.items) items.set(itemKey(item), item);
  return { po, pluralForms: po.headers["Plural-Forms"] ?? "", items };
}

/**
 * A read catalog's text as Wuchale writes it: a fresh PO with only the headers the file had, since
 * PO.parse also fills in pofile's default headers, which toString would write out.
 */
function writeCatalog(catalog) {
  const po = new PO();
  for (const name of catalog.po.headerOrder) po.headers[name] = catalog.po.headers[name];
  po.items = catalog.po.items;
  return po.toString();
}

function sameTranslation(a, b) {
  return a.msgstr.length === b.msgstr.length && a.msgstr.every((s, i) => s === b.msgstr[i]);
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

function forms(count) {
  return `${count} ${count === 1 ? "form" : "forms"}`;
}

/**
 * Why an item's forms cannot render, or null when they can. Wuchale compiles a one-form translation
 * as a string and treats a longer one as plural, so a plural entry needs exactly the language's
 * number of forms, all filled or all empty, and any other entry exactly one.
 */
function formsMismatch(item, nplurals) {
  const expected = item.msgid_plural ? nplurals : 1;
  const filled = item.msgstr.filter((s) => s !== "").length;
  if (item.msgstr.length === expected && (filled === 0 || filled === expected)) return null;
  return { expected: forms(expected), found: filled === item.msgstr.length ? forms(item.msgstr.length) : `${filled} of ${forms(item.msgstr.length)} filled` };
}

/**
 * Why the next catalog cannot replace the current one, or null when it can. The next catalog must
 * keep every item and the Plural-Forms rule (which Wuchale compiles into code), and every
 * translation it adds or changes must pass formsMismatch and mismatch. Both are read catalogs.
 */
function catalogProblems(current, next) {
  if (next.pluralForms !== current.pluralForms) return { kind: "plural-forms" };
  const missing = [...current.items.keys()].filter((key) => !next.items.has(key));
  if (missing.length > 0) return { kind: "missing", count: missing.length };

  // Wuchale's default when a catalog has no Plural-Forms (defaultPluralRule in wuchale/dist/storage.js).
  const nplurals = Number(PO.parsePluralForms(current.pluralForms).nplurals) || 2;
  const problems = [];
  for (const [key, item] of next.items) {
    const before = current.items.get(key);
    if (before && sameTranslation(before, item)) continue;
    const formsProblem = formsMismatch(item, nplurals);
    if (formsProblem) {
      problems.push({ msgid: item.msgid, msgstr: item.msgstr.join(" / "), ...formsProblem });
      continue;
    }
    const sources = item.msgid_plural ? [item.msgid, item.msgid_plural] : [item.msgid];
    item.msgstr.forEach((translation, index) => {
      const problem = translation === "" ? null : mismatch(sources, translation);
      if (!problem) return;
      problems.push({
        msgid: item.msgid,
        msgstr: translation,
        index: item.msgid_plural ? index : undefined,
        ...problem,
      });
    });
  }
  return problems.length > 0 ? { kind: "translations", problems } : null;
}

/**
 * The current catalog's text with each translation the head adds or changes against the merge base
 * written into the item of the same key, as `{ text, dropped, skipped }`. Only translations move,
 * by key, so the head's other edits (headers, comments, entries an older copy lacks) never land, and
 * a translation cannot land on another string. A translation whose string the current catalog no
 * longer has is dropped. One the current catalog changed since the merge base (or, for a string the
 * branch did not have, already translated) is skipped: the head never saw that change, and landing
 * over it would undo a correction without anyone choosing to. Null when writing the current catalog
 * back does not reproduce it, since the result would then rewrite more than the translations.
 */
function applyTranslations(mergeBase, head, currentText) {
  const current = readCatalog(currentText);
  if (writeCatalog(current) !== currentText) return null;
  let dropped = 0;
  let skipped = 0;
  for (const [key, item] of head.items) {
    const before = mergeBase.items.get(key);
    if (before && sameTranslation(before, item)) continue;
    const target = current.items.get(key);
    if (!target) {
      dropped++;
      continue;
    }
    if (sameTranslation(target, item)) continue;
    const changedOnMain = before ? !sameTranslation(before, target) : target.msgstr.some((s) => s !== "");
    if (changedOnMain) {
      skipped++;
      continue;
    }
    // A form the head skipped (msgstr[2] with no [1]) is a hole in the array; it fails formsMismatch.
    target.msgstr = Array.from(item.msgstr, (s) => s ?? "");
  }
  return { text: writeCatalog(current), dropped, skipped };
}

/**
 * Text as a code span in a table cell. A backslash in a code span is literal, so only the pipe needs
 * escaping: GitHub's table parser turns `\|` back into `|` before it reads the span.
 */
function codeSpan(text) {
  const flat = text.replace(/\s+/g, " ");
  const clipped = flat.length > 120 ? `${flat.slice(0, 117)}...` : flat;
  const longestRun = Math.max(0, ...(clipped.match(/`+/g) ?? []).map((run) => run.length));
  const fence = "`".repeat(longestRun + 1);
  return `${fence} ${clipped.split("|").join("\\|")} ${fence}`;
}

function refusal(path, problem) {
  switch (problem.kind) {
    case "plural-forms":
      return `this pull request changes the \`Plural-Forms\` line of \`${path}\`. A maintainer will review it.`;
    case "missing":
      return `this pull request removes ${problem.count} ${problem.count === 1 ? "entry" : "entries"} from \`${path}\`. A maintainer will review it.`;
    case "translations": {
      const { problems } = problem;
      const rows = problems.slice(0, MAX_LISTED_PROBLEMS).map((p) => {
        const source = p.index === undefined ? codeSpan(p.msgid) : `${codeSpan(p.msgid)} (form ${p.index})`;
        return `| ${source} | ${codeSpan(p.msgstr)} | ${codeSpan(p.expected)} | ${codeSpan(p.found)} |`;
      });
      const more = problems.length > MAX_LISTED_PROBLEMS ? `\n\nAnd ${problems.length - MAX_LISTED_PROBLEMS} more.` : "";
      return (
        [
          `${problems.length} changed ${problems.length === 1 ? "translation does" : "translations do"} not fit the English text in \`${path}\`.`,
          "",
          "A translation must keep every `<0>`, `</0>` and `<0/>` of the English text and add none. It can leave out or repeat a `{0}`, but it must not use one the English text does not have. The order can change.",
          "",
          "An entry with a plural must have every form of the language, all filled or all empty. Any other entry has one form.",
          "",
          "| English | Translation | Expected | Found |",
          "|---|---|---|---|",
          ...rows,
        ].join("\n") + more
      );
    }
  }
  throw new Error(`unknown problem ${problem.kind}`);
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

/**
 * The locale files a GitHub account may land alone. Matched by account id, since a login freed by a
 * rename can be registered by someone else. Keys starting with `$` are not files.
 */
function filesFor(allowlist, userId) {
  return Object.entries(allowlist)
    .filter(([file, translators]) => !file.startsWith("$") && translators.some((t) => t.id === userId))
    .map(([file]) => file);
}

/** The blob of a path at a commit, as `{ sha, mode }`, or null when there is no regular file there. */
async function treeBlob({ github, owner, repo, commitSha, path }) {
  const { data: commit } = await github.rest.git.getCommit({ owner, repo, commit_sha: commitSha });
  let treeSha = commit.tree.sha;
  const parts = path.split("/");
  for (const [i, part] of parts.entries()) {
    const { data: tree } = await github.rest.git.getTree({ owner, repo, tree_sha: treeSha });
    const entry = tree.tree.find((e) => e.path === part);
    if (!entry) return null;
    if (i === parts.length - 1) return entry.type === "blob" && entry.mode === "100644" ? { sha: entry.sha } : null;
    if (entry.type !== "tree") return null;
    treeSha = entry.sha;
  }
  return null;
}

async function blobText({ github, owner, repo, sha }) {
  const { data } = await github.rest.git.getBlob({ owner, repo, file_sha: sha });
  return Buffer.from(data.content, data.encoding).toString("utf8");
}

/**
 * One try at landing the head's catalog on the default branch as it is now. Returns
 * `{ landed: sha }`, `{ refused: message }`, or `{ raced: true }` when the default branch moved
 * before the commit was written.
 */
async function landOnce({ github, owner, repo, defaultBranch, path, headSha, mergeBaseSha, pr, core }) {
  const { data: ref } = await github.rest.git.getRef({ owner, repo, ref: `heads/${defaultBranch}` });
  const mainSha = ref.object.sha;

  const [headBlob, mergeBaseBlob, mainBlob] = await Promise.all(
    [headSha, mergeBaseSha, mainSha].map((commitSha) => treeBlob({ github, owner, repo, commitSha, path })),
  );
  if (!headBlob) return { refused: `\`${path}\` must stay a regular file. A maintainer will review it.` };
  if (!mergeBaseBlob || !mainBlob) return { refused: `\`${path}\` is not a regular file on \`${defaultBranch}\`. A maintainer will review it.` };

  const mainChanged = mergeBaseBlob.sha !== mainBlob.sha;
  const [mainText, headText, mergeBaseText] = await Promise.all([
    blobText({ github, owner, repo, sha: mainBlob.sha }),
    blobText({ github, owner, repo, sha: headBlob.sha }),
    mainChanged ? blobText({ github, owner, repo, sha: mergeBaseBlob.sha }) : null,
  ]);
  const current = readCatalog(mainText);
  const head = readCatalog(headText);

  // The head's translations, written into the current catalog. A catalog that pofile does not write
  // back as it is (one Wuchale does not manage) lands as the head's file instead, which is only the
  // pull request's own change while the default branch has not changed it since the branch point.
  let nextText;
  let dropped = 0;
  let skipped = 0;
  const applied = applyTranslations(mainChanged ? readCatalog(mergeBaseText) : current, head, mainText);
  if (applied) {
    ({ text: nextText, dropped, skipped } = applied);
  } else if (!mainChanged) {
    nextText = headText;
  } else {
    return {
      refused: `\`${defaultBranch}\` has changed \`${path}\` since this branch was made. Update the branch from \`${defaultBranch}\`, then push.`,
    };
  }
  if (nextText === mainText) {
    return {
      refused:
        skipped > 0
          ? `\`${defaultBranch}\` has changed every translation this pull request changes since this branch was made. Update the branch from \`${defaultBranch}\` to see them, then push.`
          : `this pull request changes no translation in \`${path}\` on \`${defaultBranch}\`.`,
    };
  }

  const problem = catalogProblems(current, readCatalog(nextText));
  if (problem) return { refused: refusal(path, problem) };

  const { data: blob } = await github.rest.git.createBlob({
    owner,
    repo,
    content: Buffer.from(nextText, "utf8").toString("base64"),
    encoding: "base64",
  });
  const { data: mainCommit } = await github.rest.git.getCommit({ owner, repo, commit_sha: mainSha });
  const { data: tree } = await github.rest.git.createTree({
    owner,
    repo,
    base_tree: mainCommit.tree.sha,
    tree: [{ path, mode: "100644", type: "blob", sha: blob.sha }],
  });
  const login = pr.user.login;
  const { data: commit } = await github.rest.git.createCommit({
    owner,
    repo,
    message: [
      `${pr.title} (#${pr.number})`,
      "",
      `Landed by translator-merge: ${login} is the allowlisted translator for ${path}.`,
      "",
      `Co-authored-by: ${login} <${pr.user.id}+${login}@users.noreply.github.com>`,
    ].join("\n"),
    tree: tree.sha,
    parents: [mainSha],
  });
  try {
    await github.rest.git.updateRef({ owner, repo, ref: `heads/${defaultBranch}`, sha: commit.sha, force: false });
  } catch (error) {
    core.info(`${defaultBranch} moved while landing: ${error.message}`);
    return { raced: true };
  }
  return { landed: commit.sha, dropped, skipped };
}

async function landIfEligible({ github, context, core, pr, allowlist, headSha }) {
  const { owner, repo } = context.repo;
  const defaultBranch = context.payload.repository.default_branch;
  const prNumber = pr.number;
  const comment = (body) => upsertComment({ github, owner, repo, prNumber, body });

  if (pr.draft) return core.info(`#${prNumber} is a draft`);

  const allowed = filesFor(allowlist, pr.user.id);
  if (allowed.length === 0) return core.info(`#${prNumber}: ${pr.user.login} is not an allowlisted translator`);
  const allowedList = allowed.map((f) => `\`${f}\``).join(", ");

  const { data: live } = await github.rest.pulls.get({ owner, repo, pull_number: prNumber });
  if (live.state !== "open" || live.head.sha !== headSha) return core.info(`#${prNumber} has moved on from ${headSha}`);

  const { data: diff } = await github.rest.repos.compareCommitsWithBasehead({
    owner,
    repo,
    basehead: `${defaultBranch}...${headSha}`,
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

  const { data: status } = await github.rest.repos.getCombinedStatusForRef({ owner, repo, ref: headSha });
  const cla = status.statuses.find((s) => s.context === CLA_CONTEXT);
  if (cla?.state !== "success") {
    return comment(
      "Not merged automatically: the Contributor License Agreement is not signed yet. Sign it, then push a commit or ask a maintainer to run the tests again.",
    );
  }

  let outcome = { raced: true };
  for (let attempt = 0; attempt < LAND_ATTEMPTS && outcome.raced; attempt++) {
    outcome = await landOnce({ github, owner, repo, defaultBranch, path, headSha, mergeBaseSha: diff.merge_base_commit.sha, pr, core });
  }
  if (outcome.raced) throw new Error(`#${prNumber}: ${defaultBranch} kept moving; run this workflow again`);
  if (outcome.refused) return comment(`Not merged automatically: ${outcome.refused}`);

  try {
    const count = (n) => `${n} ${n === 1 ? "translation was" : "translations were"}`;
    const dropped = outcome.dropped > 0 ? ` ${count(outcome.dropped)} left out because \`${defaultBranch}\` no longer has the English text.` : "";
    const skipped =
      outcome.skipped > 0
        ? ` ${count(outcome.skipped)} left out because \`${defaultBranch}\` changed them after this branch was made. Update the branch from \`${defaultBranch}\` to see them.`
        : "";
    // A push made while this ran is not in the landed commit, so the pull request stays open for it.
    const { data: after } = await github.rest.pulls.get({ owner, repo, pull_number: prNumber });
    const moved = after.head.sha !== headSha;
    const ending = moved
      ? ` This pull request stays open: it has commits after ${headSha}, which did not land.`
      : ` This pull request is closed rather than merged, because its translations are written into the catalog on \`${defaultBranch}\` as one commit.`;
    await comment(
      `Landed on \`${defaultBranch}\` as ${outcome.landed}: \`${pr.user.login}\` is the allowlisted translator for \`${path}\`, the tests passed, and the translations fit the English text.${dropped}${skipped}${ending}`,
    );
    if (!moved) await github.rest.pulls.update({ owner, repo, pull_number: prNumber, state: "closed" });
  } catch (error) {
    core.warning(`#${prNumber}: landed as ${outcome.landed}, but commenting or closing failed: ${error.message}`);
  }
  return true;
}

/**
 * Finds the open pull requests into the default branch whose head is the commit and repository
 * Tests passed on, and lands those an allowlisted translator opened. Returns whether anything
 * landed.
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
  let landed = false;
  for (const pr of prs) {
    if ((await landIfEligible({ github, context, core, pr, allowlist, headSha })) === true) landed = true;
  }
  return landed;
}

module.exports = { run, readCatalog, writeCatalog, catalogProblems, applyTranslations, filesFor, itemKey, WUCHALE_VERSION };
