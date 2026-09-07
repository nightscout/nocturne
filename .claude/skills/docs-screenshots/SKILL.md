---
name: docs-screenshots
description: "Add, re-capture or debug the screenshots the portal docs embed (@nocturne/screenshots + the <Screenshot> component). Use when a docs page needs a picture of the app, when a capture fails to settle or reports a stale anchor, when a UI change should be reflected in the docs images, or when a portal build says a screenshot id is not in the manifest. DO NOT USE for the marketing site's connect-X guides — those vendor-side shots are taken by hand."
---

# Docs screenshots

Portal docs never paste in a screenshot. Every image is declared in
`src/Web/packages/screenshots/src/manifest.ts`, captured from a running app against a seeded
tenant, and embedded by id. A stale id, a stale anchor selector or a deleted image **fails the
build** — that is the design, not a bug to work around.

**`src/Web/packages/screenshots/README.md` is the reference**: the definition format, `arrange`
vs `prepare`, `session`, ids and anchors, what a settle failure means, and the regeneration
workflow. Read it before writing a definition. This skill is only the loop around it and the
things it does not say.

## The loop

1. **Pick the shot.** One image answers one paragraph. A page-wide shot for "here is the screen",
   a `clip` for "here is the control I just described". At docs-column width a page-wide shot of a
   dense route shrinks its labels past legibility — clip rather than hope.
2. **Give the app a handle.** Anchors and clips must be `data-testid`; add one to the component if
   nothing stable exists, kebab-case like the ones already there (`public-access-card`,
   `sign-in-card`). Structural selectors are how a callout silently starts pointing at the wrong
   thing. The shadcn wrappers (`Table.Row`, `Button`, `Dialog.Content`, bits-ui triggers) all
   forward `data-testid` through `...restProps`.
3. **Declare it** in `manifest.ts`, then `pnpm --filter @nocturne/screenshots run validate` —
   definitions only, no stack and no browser, so it is the cheap first check.
4. **Embed it** in the `.svx` with `<Screenshot id="..." />`, adding the import beside `Callout`.
5. **Capture.** Nothing in steps 2-4 is proven until this runs.
6. **Commit the images and `manifest.json` with the code.** They are one change.

## Capturing

Capture drives the dev-only seeding API and photographs the dev server, so it needs a stack built
from **the tree you just edited**. A stack already running out of another worktree has none of your
testids and will fail on the clip selector.

```bash
# worktree root; the instance key doubles as the JWT signing key, so 32+ chars
ASPIRE_CLI_START_TIMEOUT=900 env "Parameters__instance-key=<32+ chars>" aspire run --isolated
```

Ports are dynamic per worktree. Do not port-scan — a local MongoDB answers 200 and looks like a
hit. Read `nocturne-api`'s URL off the aspire MCP (`list_apphosts` → `select_apphost` on *this*
worktree's AppHost → `list_resources`), and wait for the gateway to answer before capturing: the
routes are reached through it, not through the api port.

```bash
cd src/Web
NOCTURNE_API_URL=http://localhost:<api-port> pnpm --filter @nocturne/screenshots run capture
pnpm --filter @nocturne/screenshots run check-embeds   # docs ids and anchors vs the manifest
pnpm --filter @nocturne/screenshots run diff-stats     # what moved, against HEAD
```

Expect roughly a minute per entry on a cold dev server, and no output until it finishes if you
pipe it — watch `images/` mtimes for progress instead. `check-embeds` fails until the capture has
run, because the ids you just embedded are not in `manifest.json` yet; that is the expected order,
not a problem to chase.

Always run `diff-stats --restore-identical` before committing. A capture rewrites **every** image,
and without it the diff is the whole `images/` directory.

## What bites

- **A `diff-stats` percentage is a shortlist, not a verdict.** A fraction of a percent can be the
  label a docs page quotes.
- **Anchors resolve with `document.querySelector` — first match wins.** A testid that repeats per
  row is fine and points at the first one. But the box must land inside the captured frame: an
  anchor low on a long page needs `fullPage`, or a `clip` that contains it, or the run fails.
- **Both themes must agree.** Light and dark are measured separately and the run rejects an
  anchored entry whose frames differ in size or in any anchor box. A `prepare` that opens something
  of data-dependent height is the usual cause.
- **Check what a row's own `onclick` does before writing `prepare`.** On the Meals table, clicking
  the row opens the add-food dialog; only the chevron toggles the expansion.
- **`alt` is user-facing prose that reaches the rendered page.** Plain language, describe what a
  reader sees rather than what the component is called, and mind the portal's mdsvex traps before
  putting a literal `--` or a brace in it.
- **Say so when an image is noise.** Seeded totals, a minted share address, a fresh TOTP secret:
  comment it on the definition, so whoever reads the next `diff-stats` table knows which rows move
  every run regardless.
