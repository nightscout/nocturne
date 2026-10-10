---
name: measuring-dashboard-load
description: Use when a change claims to speed up (or might slow down) the web app's page load — dashboard LCP, FCP, plot draw, CLS, hydration, request timing, bundle size — and needs before/after numbers from the production images rather than the Vite dev server. Also use when a load-perf A/B result looks noisy or contradictory, or when attributing a layout shift.
---

# Measuring dashboard load

Numbers come from the production images (`e2e/` docker compose) behind a local
HTTP/2 edge that models Cloudflare: immutable assets at edge latency, everything
else through a 320 ms origin delay. The harness lives in `harness/` next to this
file and is driven from the repo root. Never measure on the Vite dev server.

## Quick reference

| Step | Command (repo root) |
|---|---|
| Build images from a checkout | `cd e2e && node scripts/stack.ts build` (`--web` / `--api` to limit) |
| Tag them as a variant | `docker tag nocturne-web:e2e nocturne-web:perf-<v>` and the same for `nocturne-api:e2e` |
| Start a fresh stack + 90-day tenant | `bash .claude/skills/measuring-dashboard-load/harness/up.sh <v>` |
| Request timeline vs long tasks and LCP | `node .claude/skills/measuring-dashboard-load/harness/ordering.cjs [cpu]` |
| Layout-shift attribution | `node .claude/skills/measuring-dashboard-load/harness/cls.cjs [cpu]` |
| A/B, alternating fresh stacks | `bash .claude/skills/measuring-dashboard-load/harness/ab.sh <a> <b> 3` (`PERF_CPU=4 SUFFIX=-x4` for throttled) |
| Compare pooled rounds | `node .claude/skills/measuring-dashboard-load/harness/summarise.cjs <a> <b>` |
| Where main-thread time goes | `node .claude/skills/measuring-dashboard-load/harness/cpuprofile.cjs <cpu> [runs]` (writes `harness/profile-x<cpu>.cpuprofile`), then `inclusive.cjs <profile>` |
| Static chunk closure of a route | `node .claude/skills/measuring-dashboard-load/harness/closure.mjs src/Web/packages/app/.svelte-kit/output/client/.vite/manifest.json "(authenticated)"` |
| Stop the stack | `PERF_HARNESS_DIR=$PWD/.claude/skills/measuring-dashboard-load/harness docker compose -p nocturne-perf -f e2e/docker-compose.yml -f .claude/skills/measuring-dashboard-load/harness/compose.perf.yml down --volumes` |

Tenant URL while a stack is up: `https://perf.nocturne.localhost:1631`. The
stack signs every visitor in through dev auto-login, standing in for the demo.

## Metrics (`perf.cjs`, pooled by `summarise.cjs`)

All wall-clock from navigation start, which includes the latency CDP adds.
`dashLcpWallMs` is the LCP of the dashboard's own content and excludes the
backup-sign-in banner a single-passkey test account gets; the plain `lcpWallMs`
mostly measures that banner. `plotWallMs` is the frame after the main plot's
glucose line first has a path. Scenarios: `signedOutCold` (auto-login redirect
chain), `signedInCold` (cookie, empty cache), `signedInWarm`.

## Discipline

- **Quiet machine.** A docker build, `dotnet test`, vitest or svelte-check
  running in parallel (including in a subagent) nearly doubles TTFB and adds
  hundreds of ms to LCP in that round. Finish implementation, then measure.
- **Alternate and read per round.** At least 3 rounds; medians across pooled
  rounds hide a disturbed round. Print each `harness/results/perf-<v>-r<n>.json`'s
  medians before trusting the pool.
- **A failed stack start truncates the round's result file to 0 bytes** and
  the pool silently drops it. Check sizes in `harness/results/` (gitignored) after `ab.sh`.
- **Model edge and origin separately.** Uniform latency on every request made
  CSS inlining look like a 380 ms win; it is ~70 ms in production.
- **One run of `ordering.cjs` before an A/B** confirms the build does what the
  commit says (a prefetch consumed, a request gone) and catches a broken image
  in minutes instead of an hour.
- **Attribute CLS before blaming a change.** Run `cls.cjs` on the baseline and
  the candidate; the `sources` rects name the element that grew.
- Chromium must start with `--host-resolver-rules=MAP *.nocturne.localhost 127.0.0.1`
  (the scripts do); without it a fresh context pays ~550 ms of DNS on Windows.
- The SSR document's `Link` header is ~20 KB; any Node proxy in front needs
  `maxHeaderSize` raised (`delay.mjs` does).

## Already measured and rejected

CSS inlining; streaming the 6-hour chart window; server-side prefetch of the
realtime store's starting reads (from the layout or scoped to the dashboard);
removing the socket-connect backfill. Re-propose only with new evidence.

## Clean-up

Stop the `nocturne-perf` stack and remove the `nocturne-*:perf-*` images when
done; a round leaves a pair per variant.
