---
name: report-videos
description: "Make, edit, re-capture or debug the promo/explainer videos of Nocturne's reports (@nocturne/video: HyperFrames films of the real report pages). Use when asked for a video of a report, to change a film's notes, points or pacing, to refresh the data a film shows, to render the videos, or when a render shows a missing tooltip, a stuck chart, a blank frame, or a failing `hyperframes check`."
---

# Report videos

`src/Web/packages/video` films the app's own report pages, unmodified, from recorded data. Read its
`README.md` first: it is the source of truth for how the bundle, fixtures, clock pin, `film()` and
the render-safety shims work. This skill is the procedure and the traps.

Vocabulary: a **film** is `src/films/<report>.js`; a **scene** frames one card; a **beat** is one
cursor stop (or scrub) with its note. Load `/hyperframes` and `/hyperframes-cli` for the framework;
this skill decides everything specific to these films.

## Make or change a film

1. **Report registered.** It is in `src/reports.js` and `src/fixtures/<report>.json` exists. If not,
   capture first (below). Then build it and run `node scripts/preview.js <report> <out.png>`: done
   when it prints the cards you mean to film, names no missing remote function the page needs, and
   the screenshot shows real data.
2. **Film written.** Each note says something true and specific that the thing under the cursor
   shows, in short plain sentences (ASD-STE100 style). Quote figures from the fixture, read off the
   tooltip, the page or the decoded fixture, never from memory. Points are `[x, y]` fractions of
   the anchor's own box, so a y-axis that runs past the data puts the line low in the box. A table,
   figure strip or card without a layerchart chart is filmed with a `selector` scene and `element`
   beats (`film()` in `src/tour/index.js` documents the shapes).
   **Show the task, not the screen.** A film that only points at a finished screen is surface level.
   Where the page lets you do something (add, edit, choose, change the range, step through days),
   film it being done with `do` beats, start to finish, ending on the result. Ask of every film:
   would someone who watched it know how to use this? See the README's Interactions section.
3. **Every beat lands.** `pnpm run build -- <report>`, then `pnpm run beats <report>` and snapshot its
   `--at` list (`pnpm exec hyperframes snapshot compositions/<report> --at …`). Done when every frame
   shows the cursor on the thing its note names, the tooltip (if the chart has one) agrees with the
   note's figure, and no tooltip covers the card's own text. Adjust points and repeat until all beats
   pass; a single miss means another loop.
4. **Check passes.** `pnpm run check` (or `node scripts/hyperframes.js check <report>`).
5. **Render verified.** `pnpm run render -- <report>`, then pull frames from the MP4 at the beat times
   with ffmpeg and look. Snapshots run one page in order; renders run parallel workers, and only the
   MP4 proves the film is deterministic.

## Films with actions

1. Write the `do` beats with selectors that hold in the live app too (`capture` performs them
   there with Playwright: `click` with `text` matches like `hasText`). Prefer ids and roles.
2. Re-capture the report (stack up, see below): done when the capture log lists the phases with
   the functions each action refetched, e.g. `after 4 action(s): ... 4:[createEntry,getTreatmentsData]`.
   The actions really run in the tenant: a film that saves something saves it there, every
   capture, so a re-capture of such a film shifts its totals and its notes need re-reading.
3. Snapshot every action beat, then the same times in reverse order: the frames must match, since
   a backward seek remounts and re-applies.

## Re-capture fixtures

Fixtures pin the data and the clock; re-capturing changes every report's figures at once.

1. Start the stack: `aspire start --isolated` in a worktree (ports are dynamic: read them with
   `aspire describe`).
2. Seed a tenant with `sampleDataDays: 30` (see `/dev-tenant-api`; insulin delivery reads 30 days).
3. `pnpm run capture https://<slug>.nocturne.localhost:<gateway-port> [report...]`. Name the reports
   to capture; each fixture keeps its own capture time, so the others are untouched.
4. Re-read the notes of every film whose fixture changed (steps 2–3 above), then re-render those.
5. Delete the tenant (`DELETE /api/v4/dev-only/admin/tenants/{id}`) and `aspire stop`.

## When a render looks wrong

| Symptom | Cause and fix |
| --- | --- |
| Highlight dots move, no tooltip box | The tooltip fades or springs on the browser clock, so every frame catches it invisible. A ready-made layerchart chart (`AreaChart`, `BarChart`, `LineChart`, `PieChart`, …) draws its default tooltip inside the library: give the new chart type a wrapper in `src/layerchart/` like `BarChart.svelte`. |
| A tooltip floats in the wrong place or over another card | It portalled out of its chart: the wrapper or `TooltipRoot` must pass `portal={false}`. |
| Chart shape eases in, highlight dots trail the tooltip, or a frame differs between workers | A `motion` tween or spring; turn it off in `Still.svelte`'s props or with a shim wrapper like `Highlight.svelte`. A sequential snapshot run can hide it: snapshot a beat on its own as well. |
| Two periods or series show identical figures, or a chart shows another day's data after an action | The page calls one remote function with different arguments. Re-capture: the fixture then keeps every answer under `calls` with its argument under `args`, and replay answers by argument. A fixture with `calls` but no `args` predates that and answers by order. |
| An action's result is missing, or the capture warns "not recorded" | The capture reads answers through a route once remote calls have been quiet for 800ms. A warning names a function that failed in the live app; fix that, then re-capture. |
| Element has zero height / unstyled | Tailwind never saw the class: `src/tour/report.css` `@source`s must cover the file using it. |
| Page renders blank or "Error loading" | A remote function the page calls is missing from the fixture (`preview` names it): re-capture. A function only an unopened dialog or an unused overlay calls stays pending harmlessly. |
| "… store not found" / missing context | The authenticated app shell normally provides it: give `src/tour/Host.svelte` an inert one, as it does the realtime store. |
| A page lists nothing it should | The page filters by the viewer's permissions or page data: the preview sets an owner viewer in `preview()`; extend that. |
| `check` says "Chrome did not send the page request" | A browser launch flake: re-run that composition. |
| `check` says "did not finish within its deadline" | HyperFrames gives the check a fixed 120s, and its audits walk every element at every sample. A page of ~12,000 elements (the actograms) cannot finish, while its seeks stay cheap. Prove such a film by its render: pull frames from the MP4 at every beat. |
| A click does nothing (menu stays shut) | The control listens for a different event, or an earlier action left the target hidden. `press()` sends pointer, mouse and click events with `detail: 1`; check the component's handlers before adding a new action kind. |
| "Execution context was destroyed" / the page goes blank after an action | Something navigated. Submits, links and history steps are blocked in `film()`; find what else left the page (a `window.location` write) and block it there. |
| A toggle or day picker does nothing | Its state lives in the URL through `$app/navigation`; the film's own `src/replay/app-navigation.js` handles in-route URLs only. |
| Black or wrong frames at the start of a film, or a frame with every note stacked | A frame was taken before the film was set up. `src/tour/index.js` holds `window.__renderReady` false until `film()` has built its timeline, and the composition template must keep its `__hfForceTimelineRebind?.()` call after registering. |
| A tooltip is missing only after a backward seek | A seek that changes the page waits through HyperFrames' `hf-seek` `waitUntil` for the page to settle, then points again. If a chart still comes too late, its data loads past `settle()`'s quiet window. |
| Rows or panels part way in or out after an action | A Svelte JS transition. `svelte/transition` and `svelte/animate` are aliased to instant stand-ins in `src/svelte/`; add any other motion the page imports there. |
| No tooltip after a sheet, dialog or select closes | bits-ui's scroll lock leaves `pointer-events: none` on the body until a timer. `hover()` clears it for its hit test; a new hit test needs the same. |
| `console_error` "attribute x: Expected length, NaN" after an action | A chart was left holding a hovered point its new data lacks. `hover()` sends `pointerleave` when the cursor leaves a chart; a chart hovered by other means needs the same. |
| `check` reports overlap/occlusion on report text | The camera, caption band and end frame layer on purpose and are already waived in `src/tour/index.js`. A new finding there is usually real: a tooltip over a card's text. Move the beat's point. |
| Root `data-duration` warning in the console | The film changed without a build: run `pnpm run build`. |
