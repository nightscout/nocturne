// node --test .github/scripts/translator-merge.test.cjs
const { test } = require("node:test");
const assert = require("node:assert/strict");
const PO = require("./vendor/pofile/po.js");
const { run, readCatalog, writeCatalog } = require("./translator-merge.cjs");

const PATH = "src/Web/locales/de.po";
const TRANSLATOR = { login: "translator", id: 42 };
const allowlist = { [PATH]: [{ login: "translator", id: 42 }] };

/**
 * A catalog as Wuchale writes it, from `[msgid, msgstr]` pairs, or `[msgid, msgid_plural, [forms]]`
 * for a plural entry.
 */
function catalog(entries, plural = "nplurals=2; plural=n == 1 ? 0 : 1;") {
  const po = new PO();
  po.headers = { "Plural-Forms": plural, Language: "de" };
  po.items = entries.map(([msgid, second, third]) => {
    const item = new PO.Item();
    item.msgid = msgid;
    if (third) item.msgid_plural = second;
    item.msgstr = third ?? [second];
    item.references = ["src/App.svelte"];
    return item;
  });
  return po.toString();
}

const BASE = catalog([
  ["Hello <0/>", ""],
  ["Error ID: {0}", "Fehler-ID: {0}"],
  ["{0} item{1}", ""],
]);
const TRANSLATED = catalog([
  ["Hello <0/>", "Hallo <0/>"],
  ["Error ID: {0}", "Fehler-ID: {0}"],
  ["{0} item{1}", "Einträge: {0}"],
]);

/**
 * A GitHub API double over a repository of commits, each a map of path to `{ text, mode }`.
 * Records what the script writes.
 */
function fakeGitHub(o = {}) {
  const commits = {
    main1: { [PATH]: { text: o.main ?? BASE, mode: "100644" }, "src/API/Program.cs": { text: "code", mode: "100644" } },
    mb1: { [PATH]: { text: o.mergeBase ?? o.main ?? BASE, mode: "100644" } },
    head1: { [PATH]: { text: o.head ?? TRANSLATED, mode: o.headMode ?? "100644" } },
  };
  const blobs = {};
  const log = { comments: [], commits: [], refs: [], closed: false, prReads: 0 };
  let refFailures = o.refFailures ?? 0;
  let main = "main1";

  const treeEntries = (sha, prefix) => {
    const seen = new Map();
    for (const [path, file] of Object.entries(commits[sha])) {
      if (!path.startsWith(prefix)) continue;
      const [name, ...rest] = path.slice(prefix.length).split("/");
      if (rest.length === 0) {
        const blob = `blob:${require("node:crypto").createHash("sha1").update(file.text).digest("hex")}`;
        blobs[blob] = file.text;
        seen.set(name, { path: name, type: "blob", mode: file.mode, sha: blob });
      } else {
        seen.set(name, { path: name, type: "tree", mode: "040000", sha: `tree:${sha}:${prefix}${name}/` });
      }
    }
    return [...seen.values()];
  };

  const github = {
    paginate: async (fn, args) => (await fn(args)).data,
    rest: {
      pulls: {
        list: async () => ({
          data: [
            {
              number: 7,
              title: "German update",
              draft: o.draft ?? false,
              user: { login: TRANSLATOR.login, id: o.userId ?? TRANSLATOR.id },
              head: { sha: "head1", repo: { full_name: "fork/nocturne" } },
            },
          ],
        }),
        // The first read is the check before landing; a later one is the check before closing.
        get: async () => {
          const sha = log.prReads++ === 0 ? (o.liveHead ?? "head1") : (o.liveHeadAfter ?? "head1");
          return { data: { state: "open", head: { sha } } };
        },
        update: async ({ state }) => {
          log.closed = state === "closed";
          return { data: {} };
        },
      },
      repos: {
        compareCommitsWithBasehead: async () => ({
          data: { files: o.files ?? [{ filename: PATH, status: "modified" }], merge_base_commit: { sha: "mb1" } },
        }),
        getCombinedStatusForRef: async () => ({
          data: { statuses: o.cla === false ? [] : [{ context: "license/cla", state: "success" }] },
        }),
      },
      git: {
        getRef: async () => ({ data: { object: { sha: main } } }),
        getCommit: async ({ commit_sha }) => ({ data: { tree: { sha: `tree:${commit_sha}:` } } }),
        getTree: async ({ tree_sha }) => {
          const [, sha, prefix] = tree_sha.match(/^tree:([^:]+):(.*)$/);
          return { data: { tree: treeEntries(sha, prefix) } };
        },
        getBlob: async ({ file_sha }) => ({ data: { content: Buffer.from(blobs[file_sha]).toString("base64"), encoding: "base64" } }),
        createBlob: async ({ content }) => {
          const sha = `new-blob-${Object.keys(blobs).length}`;
          blobs[sha] = Buffer.from(content, "base64").toString("utf8");
          return { data: { sha } };
        },
        createTree: async (args) => ({ data: { sha: "new-tree", args } }),
        createCommit: async (args) => {
          log.commits.push(args);
          return { data: { sha: `landed-${log.commits.length}` } };
        },
        updateRef: async (args) => {
          if (refFailures-- > 0) throw new Error("Update is not a fast forward");
          log.refs.push(args);
          return { data: {} };
        },
      },
      issues: {
        listComments: async () => ({ data: [] }),
        createComment: async ({ body }) => {
          log.comments.push(body);
          return { data: {} };
        },
        updateComment: async () => ({ data: {} }),
      },
    },
  };
  // The tree the script asks for, so a test can read the blob that would land.
  const createTree = github.rest.git.createTree;
  github.rest.git.createTree = async (args) => {
    log.tree = args;
    log.landedText = blobs[args.tree[0].sha];
    return createTree(args);
  };
  return { github, log };
}

async function land(o) {
  const { github, log } = fakeGitHub(o);
  const core = { info() {}, warning() {} };
  const context = { repo: { owner: "o", repo: "r" }, payload: { repository: { default_branch: "main" } } };
  const landed = await run({ github, context, core, allowlist, headSha: "head1", headRepository: o?.headRepository ?? "fork/nocturne" });
  return { landed, ...log };
}

const translations = (text) => Object.fromEntries([...readCatalog(text).items.values()].map((i) => [i.msgid, i.msgstr[0]]));

test("writes the translations as one commit on main, with only the catalog replaced", async () => {
  const r = await land();
  assert.equal(r.landed, true);
  assert.equal(r.landedText, TRANSLATED);
  assert.deepEqual(r.tree.tree, [{ path: PATH, mode: "100644", type: "blob", sha: r.tree.tree[0].sha }]);
  assert.equal(r.tree.base_tree, "tree:main1:");
  assert.deepEqual(r.commits[0].parents, ["main1"]);
  assert.match(r.commits[0].message, /Co-authored-by: translator <42\+translator@users\.noreply\.github\.com>/);
  assert.equal(r.refs[0].force, false);
  assert.equal(r.closed, true);
});

test("keeps strings main added since the branch point", async () => {
  const main = catalog([
    ["Hello <0/>", ""],
    ["Error ID: {0}", "Fehler-ID: {0}"],
    ["{0} item{1}", ""],
    ["New string", ""],
  ]);
  const r = await land({ main, mergeBase: BASE });
  assert.equal(r.landed, true);
  assert.deepEqual(translations(r.landedText), {
    "Hello <0/>": "Hallo <0/>",
    "Error ID: {0}": "Fehler-ID: {0}",
    "{0} item{1}": "Einträge: {0}",
    "New string": "",
  });
});

test("an older copy that lacks entries does not remove them", async () => {
  const head = catalog([["Hello <0/>", "Hallo <0/>"]]);
  const r = await land({ head });
  assert.equal(r.landed, true);
  assert.equal(Object.keys(translations(r.landedText)).length, 3);
});

test("leaves out a translation whose string main removed, and says so", async () => {
  const main = catalog([
    ["Hello <0/>", ""],
    ["Error ID: {0}", "Fehler-ID: {0}"],
  ]);
  const r = await land({ main, mergeBase: BASE });
  assert.equal(r.landed, true);
  assert.deepEqual(translations(r.landedText), { "Hello <0/>": "Hallo <0/>", "Error ID: {0}": "Fehler-ID: {0}" });
  assert.match(r.comments[0], /1 translation was left out/);
});

test("keeps main's header, so the head cannot change Plural-Forms", async () => {
  const head = catalog([["Hello <0/>", "Hallo <0/>"]], "nplurals=2; plural=n == 1 ? (n=>n(n))(n=>n(n)) : 1;");
  const r = await land({ head });
  assert.equal(r.landed, true);
  assert.equal(readCatalog(r.landedText).pluralForms, "nplurals=2; plural=n == 1 ? 0 : 1;");
});

test("ignores a pull request from an account not on the list, whatever its login", async () => {
  const r = await land({ userId: 99 });
  assert.equal(r.landed, false);
  assert.equal(r.comments.length, 0);
  assert.equal(r.commits.length, 0);
});

test("ignores a run from another head repository", async () => {
  const r = await land({ headRepository: "other/nocturne" });
  assert.equal(r.landed, false);
  assert.equal(r.commits.length, 0);
});

test("does nothing when the pull request moved on from the tested commit", async () => {
  const r = await land({ liveHead: "head2" });
  assert.equal(r.landed, false);
  assert.equal(r.commits.length, 0);
});

test("refuses a change to any other file", async () => {
  const r = await land({
    files: [
      { filename: PATH, status: "modified" },
      { filename: "src/API/Program.cs", status: "modified" },
    ],
  });
  assert.equal(r.landed, false);
  assert.match(r.comments[0], /must change only `src\/Web\/locales\/de.po`/);
});

test("refuses without a signed CLA", async () => {
  const r = await land({ cla: false });
  assert.equal(r.landed, false);
  assert.match(r.comments[0], /Contributor License Agreement/);
});

test("refuses a catalog that is a symlink", async () => {
  const r = await land({ headMode: "120000" });
  assert.equal(r.landed, false);
  assert.match(r.comments[0], /must stay a regular file/);
});

test("refuses a translation that drops a markup slot, and lists it", async () => {
  const head = catalog([["Hello <0/>", "Hallo"]]);
  const r = await land({ head });
  assert.equal(r.landed, false);
  assert.match(r.comments[0], /1 changed translation does not fit the English text/);
  assert.match(r.comments[0], /\| ` Hello <0\/> ` \| ` Hallo ` \| ` <0\/> ` \| ` none ` \|/);
});

test("refuses a translation that invents a hole, but allows leaving one out", async () => {
  assert.equal((await land({ head: catalog([["{0} item{1}", "Einträge: {0}"]]) })).landed, true);
  const r = await land({ head: catalog([["Error ID: {0}", "Fehler-ID: {0} {2}"]]) });
  assert.equal(r.landed, false);
});

test("reads a stray msgstr after a blank line as pofile does", async () => {
  const head = BASE.replace('msgstr "Fehler-ID: {0}"', 'msgstr "Fehler-ID: {0}"\n\nmsgstr "Fehler <7/> {9}"');
  const r = await land({ head });
  assert.equal(r.landed, false);
  assert.match(r.comments[0], /does not fit the English text/);
});

test("refuses a pull request that changes no translation", async () => {
  const r = await land({ head: BASE });
  assert.equal(r.landed, false);
  assert.match(r.comments[0], /changes no translation/);
});

test("tries again when main moves while landing", async () => {
  const r = await land({ refFailures: 2 });
  assert.equal(r.landed, true);
  assert.equal(r.commits.length, 3);
});

test("a catalog pofile does not write back lands as the head's file only while main has not changed it", async () => {
  const unmanaged = "# A comment pofile drops\n" + BASE;
  const head = "# A comment pofile drops\n" + TRANSLATED;
  const unchanged = await land({ main: unmanaged, mergeBase: unmanaged, head });
  assert.equal(unchanged.landed, true);
  assert.equal(unchanged.landedText, head);

  const changed = await land({ main: unmanaged + "\n", mergeBase: unmanaged, head });
  assert.equal(changed.landed, false);
  assert.match(changed.comments[0], /Update the branch/);
});

test("does not undo a translation main corrected after the branch was made", async () => {
  // The branch landed "Fehler-ID: {0}" before; main then corrected it, and the same branch comes back.
  const mergeBase = catalog([
    ["Hello <0/>", ""],
    ["Error ID: {0}", ""],
  ]);
  const main = catalog([
    ["Hello <0/>", ""],
    ["Error ID: {0}", "Fehlerkennung: {0}"],
  ]);
  const head = catalog([
    ["Hello <0/>", "Hallo <0/>"],
    ["Error ID: {0}", "Fehler-ID: {0}"],
  ]);
  const r = await land({ main, mergeBase, head });
  assert.equal(r.landed, true);
  assert.deepEqual(translations(r.landedText), { "Hello <0/>": "Hallo <0/>", "Error ID: {0}": "Fehlerkennung: {0}" });
  assert.match(r.comments[0], /1 translation was left out because `main` changed them/);

  const onlyCorrected = await land({ main, mergeBase, head: catalog([["Hello <0/>", ""], ["Error ID: {0}", "Fehler-ID: {0}"]]) });
  assert.equal(onlyCorrected.landed, false);
  assert.match(onlyCorrected.comments[0], /has changed every translation/);
});

test("a plural entry needs every form of the language, all filled or all empty", async () => {
  const base = catalog([["# day", "# days", ["", ""]]]);
  const plural = (forms) => land({ main: base, head: catalog([["# day", "# days", forms]]) });
  assert.equal((await plural(["# Tag", "# Tage"])).landed, true);
  for (const forms of [["# Tage"], ["", "# Tage"], ["# Tag", "# Tage", "# Tagen"]]) {
    const r = await plural(forms);
    assert.equal(r.landed, false, JSON.stringify(forms));
    assert.match(r.comments[0], /does not fit/);
  }
});

test("an entry without a plural has one form", async () => {
  const r = await land({ head: catalog([["Error ID: {0}", "Error ID: {0}", ["Fehler-ID: {0}", "x"]]]).replace('msgid_plural "Error ID: {0}"\n', "") });
  assert.equal(r.landed, false);
  assert.match(r.comments[0], /does not fit/);
});

test("refuses a plural with a skipped form instead of failing", async () => {
  const base = catalog([["# day", "# days", ["", ""]]]);
  const head = base.replace('msgstr[0] ""\nmsgstr[1] ""', 'msgstr[0] "# Tag"\nmsgstr[2] "# Tage"');
  const r = await land({ main: base, head });
  assert.equal(r.landed, false);
  assert.match(r.comments[0], /does not fit/);
});

test("leaves the pull request open when it was pushed to while landing", async () => {
  const r = await land({ liveHeadAfter: "head2" });
  assert.equal(r.landed, true);
  assert.equal(r.closed, false);
  assert.match(r.comments[0], /stays open/);
});

test("the catalogs Wuchale writes round-trip through writeCatalog", () => {
  const fs = require("node:fs");
  const path = require("node:path");
  const text = fs.readFileSync(path.join(__dirname, "../../src/Web/locales/en.po"), "utf8");
  assert.equal(writeCatalog(readCatalog(text)), text);
});
