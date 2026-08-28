# @nocturne/screenshots

Captures the screenshots the documentation embeds, so they are regenerated from a running app
rather than pasted in by hand and left to rot.

`src/manifest.ts` declares what to shoot. A run seeds a throwaway dev tenant per scenario, signs a
browser in, and writes `images/{id}.{theme}.webp` plus `manifest.json` — alt text, image dimensions
and anchor boxes, keyed by id. `src/types.ts` is the contract the docs-side consumer reads.

## Running a capture

Needs a local stack (`aspire start` from the repo root) — capture drives the dev-only seeding API,
which only exists when the API runs in Development — and a browser binary, which pnpm does not
install for you:

```bash
pnpm --filter @nocturne/screenshots exec playwright install chromium  # once per machine
pnpm --filter @nocturne/screenshots run capture                       # seed, shoot, write manifest.json
pnpm --filter @nocturne/screenshots run validate                      # definitions only; no stack, no browser
pnpm --filter @nocturne/screenshots run check-refs                    # markdown references vs images on disk
pnpm --filter @nocturne/screenshots run check-embeds                  # docs <Screenshot> ids and anchors vs the manifest
pnpm --filter @nocturne/screenshots run diff-stats                    # what a capture just changed, against HEAD
```

`diff-stats` reads the working tree against the committed images and prints a markdown table of
how much of each one moved, plus anything added, deleted, resized or unreadable. `--output <file>`
writes the same table to disk; `--restore-identical` puts committed bytes back for images whose
pixels did not change, so an encoder re-quantising the same render never shows up as drift. A
percentage is a shortlist of what to look at, not a verdict: a fraction of a percent can still be
the label a docs page quotes.

A successful run tidies up after itself: images no manifest entry claims are deleted, and the
tenants it seeded are removed. A failed one leaves both behind to be inspected.

`NOCTURNE_API_URL` overrides the API base (default `http://localhost:1610`); a worktree gets dynamic
ports, so read yours from `aspire describe`.

Locale, timezone, viewport, device scale and the browser clock are pinned in `src/capture.ts` and
must stay that way: a screenshot is only reviewable as a diff against the one it replaces.

## Regeneration workflow

`.github/workflows/screenshot-regen.yml` boots a stack on the runner, runs the capture, and opens
a PR on `docs/screenshot-regen` with the `diff-stats` table in its body when the images move.
Dispatch it from the Actions tab ("Documentation screenshots" → Run workflow) after a UI change
you expect the docs to show. It is dispatch-only: the seeded data is anchored to the calendar
date, so an unattended schedule would drift on every run and bury real UI changes in data noise —
a schedule becomes viable once seeding can replay identical data shapes across days.

A capture failure — a stale anchor, a route that will not settle — fails the run and opens
nothing; the capture log and whatever images it managed to write are attached to the run as an
artifact.

Two GitHub settings caveats: creating the PR at all requires "Allow GitHub Actions to create and
approve pull requests" (Settings → Actions → General), and GitHub does not run workflows on a PR
opened with `GITHUB_TOKEN`, so it arrives with no checks — close and reopen it to start them.

## When a run fails to settle

A capture is only taken once the route has been free of skeletons, "Loading" text, coach marks and
in-flight remote queries for an unbroken moment; otherwise the entry fails with what was still
outstanding, e.g.

```
alerts-configuration: /alerts did not settle within 120000ms (34 skeleton placeholders; 8 remote queries in flight)
```

Remote queries stuck in flight while the API itself answers them in milliseconds is the dev server,
not the app: a `vite dev` that has been up for hours, through several dependency re-optimisations,
starts accepting remote-function requests and never answering them. Stopping and starting
`nocturne-web` clears it.

## Ids and anchors

An `id` is a permanent handle. Docs pages point at it, so adding is safe and renaming is not.

`anchors` name the parts of a screenshot a docs callout can point at, mapping a name to a CSS
selector. Capture records each one's box in image pixels, relative to the image's own top-left, so
the docs side can draw a numbered marker without knowing anything about the app's markup. Prefer
`data-testid` over structural selectors, and add one to the app if nothing stable exists.

A selector that matches nothing **fails the run**. That is the point: it means the UI moved and the
callout — and probably the prose around it — is now describing something that is not there.
