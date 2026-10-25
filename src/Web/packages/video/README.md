# @nocturne/video

Promotional video, rendered with [HyperFrames](https://hyperframes.heygen.com) from HTML. Each
directory under `compositions/` is one HyperFrames project. Only `logo` is kept by hand; the build
writes every other one from its film (`scripts/composition.js`), so a report is just its film and
its fixture:

| Composition            | What it is                                                           |
| ---------------------- | -------------------------------------------------------------------- |
| `logo`                 | The animated logomark, 1080×1080 (`src/logo/`).                       |
| `agp`                  | Explainer: the AGP's median, peaks and bands, then time in range.    |
| `glucose-distribution` | Explainer: the ranges in the donut, then range by hour of the day.   |
| `basal-analysis`       | Explainer: basal rate by hour and its spread, then hourly delivery.  |
| `insulin-delivery`     | Explainer: basal and bolus day by day, then insulin by hour.         |
| `idp`                  | Explainer: time in range, the Glycemic Risk Index, then the day view. |

The report films run 45 to 65 seconds: a title card, a few scenes in which the cursor stops on
each point of interest while a note explains it, and the logo animation, with getnocturne.dev
under the wordmark. The address also sits in the bottom-right corner until the logo arrives. The
logo animation lives in `src/logo/` and is shared by the `logo` composition and every film's end.

## Running

```bash
pnpm --filter @nocturne/video run render            # every composition, to renders/<name>.mp4
pnpm --filter @nocturne/video run render -- idp     # just one
pnpm --filter @nocturne/video run check             # lint, layout, motion and contrast
pnpm --filter @nocturne/video run preview -- compositions/idp   # HyperFrames Studio, after a build
```

`scripts/build.js`, `scripts/preview.js` and `scripts/beats.js` take `--features` to work on the feature films
(`compositions/feature-<name>/`) in place of the reports.

`render` and `check` build first. The build needs the app's generated API client: run the API
build once (`aspire start` or `dotnet build src/API/Nocturne.API`) if
`src/Web/packages/app/src/lib/api/generated/` is missing. HyperFrames downloads its own headless
Chrome on first run.

## How the report films work

Each report composition mounts the app's own reports `+layout.svelte` with the report's
`+page.svelte` inside it, unmodified, and films it from one paused GSAP timeline.

- **Data.** `src/fixtures/<report>.json` holds what each of the page's remote functions returned
  on a running stack, devalue-encoded. The build replaces every `*.remote.ts` module with
  stand-ins named after its exports that answer from the fixture, so no server code is bundled.
  A function the page called with several arguments (a comparison's two periods) keeps every
  answer with the argument it answered, and replay matches on the argument.
  `$app/state` and `$app/navigation` resolve to the film's own (`src/replay/`), so a page that
  keeps state in its URL re-renders; the rest of `$app/*` resolves to the app's test stubs, as in
  the browser test suite. `vite.base.js` holds all of this for both report and feature bundles;
  `vite.config.js` adds only which layout, page and fixture a report mounts. The host gives the page
  what the authenticated app shell would: an owner viewer (`effectivePermissions: ["*"]`) and a
  realtime store that is never connected.
- **Clock.** The browser clock is pinned to the instant the report's fixture was captured, so a
  page's default date range and its "last updated" line match the data on every frame and every
  render worker.
- **Film.** `src/films/<report>.js` is the report's script: a title card, then scenes. A scene
  frames one card by title (`card`), or any element by selector (`selector`), or with `focus` some
  of its charts, under a heading, and runs through beats. A beat rests the cursor on a point
  (`at`), scrubs it along a path (`path`), or rests on an element in a chart (`on`), on one of the
  scene's charts, or on any element in the scene (`element`), while its note shows; `film()` in
  `src/tour/index.js` documents the exact shapes. A note stays
  up long enough to read, so the length of a film follows from its words; `src/tour/timing.js`
  schedules it, and the build writes the total into the composition's root `data-duration`. Notes quote figures from the fixture, so
  a re-capture means re-reading them.

### Interactions

A beat can act as well as point: `do: [{ click, text? } | { type, text } | { choose, value }]`
(`film()` in `src/tour/index.js` has the exact shapes). That is how a film shows a task done
start to finish: open a menu, pick an item, fill in the dialog it opens, save, and see the result.

- **The page at any moment** is a fresh mount with every action up to then applied, so a frame
  depends only on its time. Seeking backwards remounts and re-applies.
- **Data after an action.** When a film has actions, `capture` performs them in the live app and
  records what the app answered after each one, under `phases`; replay moves to the next phase as
  each action runs, so a save shows what it saved. A function called with several arguments keeps
  each answer with its argument, and replay answers by argument. Those actions really happen in
  the tenant.
- **Rehearsal.** At setup the film runs its actions once, with the camera where each scene has it,
  to measure what a scene frames and where each action's target is. The cursor goes to that
  rehearsed spot: a menu is placed asynchronously, so its live position is wrong within the seek
  that opens it.
- **Never leaving the page.** Form submissions, link navigations and history steps are cancelled
  (dialogs that close with the back button call `history.back()`), and `$app/state` and
  `$app/navigation` are the film's own: `page` is reactive, and a `goto` within the page's route
  changes `page.url` in place, so state kept in the URL (a toggle, the day shown) re-renders.
- **Dialogs and popovers** portal to the document body and overlay the frame at their natural size,
  kept above the caption band.

HyperFrames renders by seeking that timeline to each frame's time, out of order and across
parallel workers, so every frame must follow from its time alone:

- **Pointer state.** On each frame `film` dispatches a `pointermove` at that frame's cursor
  position and calls `flushSync()`, so layerchart draws its tooltip and highlights for exactly
  that point. Only the chart under the cursor shows them.
- **layerchart motion.** `src/layerchart/` stands in for `layerchart` in every import: a tooltip
  has no spring, fade or portal, a highlight jumps rather than springs, and charts draw without
  tweening in. The ready-made charts
  (`AreaChart`, `BarChart`, `LineChart`, `PieChart`) draw their default tooltip from inside
  layerchart, where the shim cannot reach, so their wrappers pass the same settings in through
  `props.tooltip.root` and `props.highlight`. A new chart type a report uses needs the same wrapper.
- **CSS transitions.** Every transition and animation in the report is off, and Svelte's JS
  transitions and animations finish at once (`src/svelte/`).
- **Readiness.** `window.__renderReady` reads false until `film()` has built its timeline, so no
  frame is taken while it rehearses. A seek that changes the page holds its frame, through
  HyperFrames' `hf-seek` event, until the page has settled and the cursor points again.

## Adding a report

1. Add it to `src/reports.js`.
2. Capture its fixture. Seed a dev tenant and pass its gateway URL and the report's name
   (worktree ports come from `aspire describe`):

   ```bash
   curl -X POST http://localhost:1610/api/v4/dev-only/admin/seed-tenant \
     -H "Content-Type: application/json" \
     -d '{"slug":"promo","displayName":"Promo","ownerUsername":"dev","sampleData":true,"sampleDataDays":30}'
   pnpm --filter @nocturne/video run capture https://promo.nocturne.localhost:1612 <report>
   ```

   With no names it re-captures every report, and each film's notes then need re-reading. The
   capture scrolls the page to the bottom, so cards that load on sight are recorded too. Report
   pages read raw glucose entries only to count them, so each entry keeps just its time and value.
3. Build it (`pnpm run build -- <report>`) and run `node scripts/preview.js <report> <out.png>`: it
   mounts the page with no film, lists each card's title and chart count, names any remote function
   the fixture lacks, and saves a full-page screenshot.
4. Write `src/films/<report>.js`. Build (which writes `compositions/<report>/`), and snapshot the moment each beat is on screen:
   `pnpm run beats <report>` prints the times and the `--at` list for
   `pnpm exec hyperframes snapshot compositions/<report>`. Check that every point lands where its
   note says. The `report-videos` skill (`.claude/skills/`) has the full loop and the usual faults.
