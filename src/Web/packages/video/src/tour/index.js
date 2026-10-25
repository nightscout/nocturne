import "../replay/clock.js";
import "./report.css";
import "./stage.css";
import { flushSync, mount, unmount } from "svelte";
import { page } from "$app/state";
import { film as script, fixture } from "virtual:report";
import { setPhase, useFixture } from "../replay/remote.js";
import { CAMERA, KEY, LOGO_IN, actionTimes, schedule } from "./timing.js";
import { buildLogo } from "../logo/index.js";
import Host from "./Host.svelte";

const W = 1920;
const H = 1080;
/** During scenes the report shows only above this line; the heading and notes sit in the band below it. */
const STAGE = 860;
const ENTRY = { screen: [1560, 1000] };
const URL_TEXT = "getnocturne.dev";

/**
 * The renderer takes a frame once `window.__renderReady` is true, which its runtime sets as soon as
 * the page has loaded, unaware of a film still rehearsing. Until `film` has built its timeline the
 * flag reads false, so no frame shows the page half set up.
 */
let runtimeReady = window.__renderReady ?? false;
let filmReady = false;
Object.defineProperty(window, "__renderReady", {
  configurable: true,
  get: () => runtimeReady && filmReady,
  set: (value) => {
    runtimeReady = value;
  },
});

const CURSOR = `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 2.5 L4 19.5 L8.6 15.4 L11.6 22 L14.6 20.7 L11.7 14.2 L18 14.2 Z" fill="#fff" stroke="#111" stroke-width="1.4" stroke-linejoin="round" /></svg>`;

/**
 * Films the page from its script: a title card over the whole page, then each scene in turn, where
 * the camera frames part of the page and the cursor works through the scene's beats, then a pull
 * back into the animated logo. Resolves to `{ timeline }`, the paused timeline for the composition
 * to register: wrapped, because a GSAP timeline is a thenable and awaiting one that never plays
 * never resolves.
 *
 * A scene is `{ card | selector, heading, beats, focus?, fill? }`: `card` is the start of a card's
 * title, `selector` a CSS selector for any other element of the page to frame, and `focus` the
 * indexes of the charts in it to frame instead, for one too tall to film whole.
 *
 * A beat is `{ note, chart? | element?, at? | path? | on?, do? }`. Its anchor is chart `chart`
 * (default 0) of the scene, or the first element `element` selects in it, for a table, figure or
 * card with no chart. `at` is a point to rest on and `path` points to scrub through, both as
 * `[x, y]` fractions of the anchor's size; `on` is a selector for an element in a chart to rest on
 * the middle of. An element beat with neither rests on the element's middle.
 *
 * `do` is what the cursor does once it arrives, in order: `{ click: selector, text? }` presses the
 * first visible match (whose text contains `text`, when given), `{ type: selector, text }` types
 * `text` into a field a character at a time, `{ choose: selector, value }` sets a native select.
 * Their targets are looked up anywhere in the document when the action runs, so they reach a
 * dialog an earlier click opened; a beat with `do` and no anchor of its own points at the first
 * target, and the cursor moves from target to target. The page at any moment is a fresh mount with
 * every action up to then applied in order, so a frame depends only on its time.
 */
export async function film(root) {
  const { intro, scenes } = script;
  const timing = schedule(script);
  const acts = scenes.some((scene) => scene.beats.some((beat) => beat.do?.length));

  // The camera carries the page past the viewport's edges on purpose, which the layout audit is
  // told; the title card and the logo cover it on purpose too.
  const viewport = el("div", "tour-viewport", root);
  const camera = el("div", "tour-camera", viewport);
  camera.dataset.layoutAllowOverflow = "";
  const target = el("div", "tour-report", camera);
  const cursor = el("div", "tour-cursor", root);
  cursor.innerHTML = CURSOR;

  // Every element the film points at is found again after a remount: `ref` re-runs its lookup
  // once per mount, and until it finds something, since a page still loading may not have it yet.
  let generation = 0;
  // An action's target is the element the action ran on: once that leaves the page (a menu item,
  // once chosen) it is gone rather than looked up again, which could match another element.
  const ref = (find, { once = false } = {}) => {
    let seen = -1;
    let found = null;
    return () => {
      if (seen !== generation || !found || (!found.isConnected && !once)) {
        found = find();
        seen = generation;
      }
      return found?.isConnected ? found : null;
    };
  };

  // A film never leaves its page: a submitted form, a followed link or a history step (dialogs that
  // close with the back button call history.back()) would replace the whole composition. The page's
  // own handlers still run; only the browser's navigation is cancelled.
  for (const step of ["back", "forward", "go"]) history[step] = () => {};
  // Nor does focus scroll it: a dialog that closes hands focus back to its trigger, which would scroll
  // the page's own scroll box away from where rehearsal measured it. The camera alone frames the page.
  const focus = HTMLElement.prototype.focus;
  HTMLElement.prototype.focus = function (options) {
    return focus.call(this, { ...options, preventScroll: true });
  };
  document.addEventListener("submit", (event) => event.preventDefault(), true);
  document.addEventListener("click", (event) => {
    if (event.target instanceof Element && event.target.closest("a[href]")) event.preventDefault();
  }, true);

  // Every mount starts with empty browser storage: a page that keeps a choice there (the simulator's
  // rule toggles, in sessionStorage) would otherwise carry an action into the mount after a rewind.
  const freshStorage = () => {
    sessionStorage.clear();
    localStorage.clear();
  };
  freshStorage();
  let component = preview(target);
  await settle(target);
  prepareForAudit(target);

  if (Number(root.dataset.duration) !== timing.total) {
    console.error(`[tour] data-duration is ${root.dataset.duration}; the film runs ${timing.total}s. Run the build.`);
  }

  // Each scene's part of the page, and each beat's points, as lookups rather than elements.
  const report = ref(() => target.querySelector("[data-report-root]") ?? target.firstElementChild);
  const plans = scenes.map((scene, i) => {
    const sceneEl = ref(() => (scene.card ? card(target, scene.card) : target.querySelector(scene.selector)));
    const charts = () => sceneEl()?.querySelectorAll(".lc-root-container") ?? [];
    const heading = el("div", "tour-heading", root);
    heading.textContent = scene.heading;
    const beats = scene.beats.map((beat, j) => {
      const times = timing.planned[i].beats[j];
      const { starts } = actionTimes(beat.do);
      const actions = (beat.do ?? []).map((spec, k) => ({ spec, t: times.arrive + starts[k], target: ref(() => reveal(find(spec)), { once: true }) }));
      const anchor = beat.element
        ? ref(() => sceneEl()?.querySelector(beat.element))
        : beat.chart !== undefined || !actions.length || beat.at || beat.path || beat.on
          ? ref(() => charts()[beat.chart ?? 0])
          : null;
      const points = anchor
        ? pointsOf(beat, anchor)
        : [{ anchor: actions[0].target, fx: 0.5, fy: 0.5, act: true, action: actions[0] }];
      const note = el("div", "tour-note", root);
      note.textContent = beat.note;
      return { ...times, note, points, actions };
    });
    return { ...timing.planned[i], scene, sceneEl, charts, heading, beats };
  });
  const actions = plans.flatMap((plan) => plan.beats.flatMap((beat) => beat.actions));

  // What a chart shows depends only on the point, so a frame pointing where the last one did has
  // nothing to redo; on a page of thousands of marks that is most of a seek's cost.
  let hovered = null;
  let lastPoint = null;
  // The element the last pointer event went to, which is told when the cursor leaves its chart.
  let left = null;

  // The page is a fresh mount with every action up to `t` applied in order. Going forward applies
  // the new ones; going back starts again from a fresh mount.
  let applied = 0;
  const dueAt = (t) => {
    let due = 0;
    while (due < actions.length && actions[due].t <= t) due++;
    return due;
  };
  /** Whether actions due at `t` are still to be applied, after the page settles from the last one. */
  const behind = (t) => applied < dueAt(t);
  /**
   * Applies the actions due at `t`, one per call: a page answers an action over several turns (a
   * dialog mounting its content, a list filtering), and the next action may need what it shows, as
   * a person waits for it. A seek that lands several actions on applies the rest as the page
   * settles (see `catchUp`), so a frame shows the same page whatever was seeked before it.
   */
  function stateAt(t) {
    if (!actions.length) return false;
    const before = applied;
    const due = dueAt(t);
    if (due < applied) {
      unmount(component);
      freshStorage();
      component = preview(target);
      generation++;
      hovered = null;
      lastPoint = null;
      left = null;
      applied = 0;
      prepareForAudit(target);
      // A fresh mount loads over several turns; its first action waits for it, as the rest do.
      return true;
    }
    // Replay answers each action's requests, including those it sends as it runs, as the capture
    // did: from the app as it was after that action, so a save shows what it saved.
    if (applied < due) {
      setPhase(applied + 1);
      act(actions[applied], t);
      applied++;
      flushSync();
    }
    const typing = actions[due - 1];
    if (applied === due && typing?.spec.type !== undefined) type(typing, t);
    return applied !== before;
  }

  /** Brings the page to its state at `t`, letting it settle after each action. */
  async function catchUp(t) {
    stateAt(t);
    await settle(document.body);
    while (behind(t)) {
      stateAt(t);
      await settle(document.body);
    }
  }

  function act(action, t) {
    const node = action.target();
    if (!node) {
      console.warn(`[tour] nothing to act on for ${JSON.stringify(action.spec)}`);
      return;
    }
    const { spec } = action;
    if (spec.type !== undefined) type(action, t);
    else if (spec.choose !== undefined) {
      node.value = spec.value;
      node.dispatchEvent(new Event("input", { bubbles: true }));
      node.dispatchEvent(new Event("change", { bubbles: true }));
    } else press(node);
    flushSync();
  }

  function type(action, t) {
    const node = action.target();
    if (!node) return;
    const { text } = action.spec;
    const shown = text.slice(0, Math.min(text.length, Math.floor((t - action.t) / KEY) + 1));
    if (node.value === shown) return;
    node.focus?.({ preventScroll: true });
    const setter = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(node), "value")?.set;
    // Through the prototype's setter, as typing does, so frameworks tracking the value see the change.
    if (setter) setter.call(node, shown);
    else node.value = shown;
    node.dispatchEvent(new Event("input", { bubbles: true }));
    flushSync();
  }

  // Rehearse the film once, so a scene that frames something an earlier action reveals is
  // measured with it there; then start again from a fresh mount.
  // Each action records where its target is on screen just before it runs, which is where the
  // cursor goes for it (see `spot`). A scene's actions are rehearsed with the camera where the scene
  // has it: a menu is placed against its trigger, so where it lands depends on the camera.
  const shots = [];
  const marks = [
    ...plans.map((plan, i) => ({ t: plan.start, scene: i })),
    ...actions.map((action) => ({ t: action.t - 1e-6, action })),
  ].sort((a, b) => a.t - b.t);
  for (const mark of marks) {
    if (mark.action) {
      // Menus and popovers are placed asynchronously (floating-ui), and portal outside the page.
      await catchUp(mark.t);
      mark.action.fallback = rehearsedSpot(mark.action.target());
      continue;
    }
    window.gsap.set(camera, { x: 0, y: 0, scale: 1 });
    if (acts) await catchUp(mark.t);
    const plan = plans[mark.scene];
    const framed = plan.scene.focus ? plan.scene.focus.map((n) => plan.charts()[n]) : [plan.sceneEl()];
    if (!framed.every(Boolean)) throw new Error(`Nothing to frame for "${plan.scene.card ?? plan.scene.selector}"`);
    shots[mark.scene] = shot(framed, plan.scene.fill ?? 0.74, STAGE / 2, plan.scene.focus ? 48 : 0);
    if (acts) window.gsap.set(camera, shots[mark.scene]);
  }
  window.gsap.set(camera, { x: 0, y: 0, scale: 1 });
  if (acts) {
    stateAt(-1);
    await settle(target);
    prepareForAudit(target);
  }
  const overview = shot([report()], 0.94, H / 2);

  const titleCard = el("div", "tour-title", root);
  titleCard.dataset.layoutAllowOcclusion = "";
  const titleText = fill(titleCard, intro.title, intro.line);
  const watermark = el("div", "tour-url", root);
  watermark.textContent = URL_TEXT;
  const end = el("div", "tour-end", root);
  end.dataset.layoutAllowOcclusion = "";
  const logo = buildLogo(end, { url: URL_TEXT });
  // The logo's background hides the page entirely, but the audit still pairs its text with the
  // page text beneath.
  for (const node of logo.root.querySelectorAll(".wordmark, .url")) node.dataset.layoutAllowOverlap = "";

  const tl = window.gsap.timeline({ paused: true });
  tl.fromTo(camera, { ...overview, scale: overview.scale * 0.96, opacity: 0 }, { ...overview, opacity: 1, duration: 1.2, ease: "power3.out" }, 0);
  tl.fromTo(watermark, { autoAlpha: 0 }, { autoAlpha: 1, duration: 0.8 }, 0.3);
  tl.fromTo(titleCard, { autoAlpha: 0 }, { autoAlpha: 1, duration: 0.8, ease: "power2.out" }, 0.3);
  tl.fromTo(titleText, { y: 30 }, { y: 0, duration: 1, ease: "power3.out" }, 0.3);
  tl.to(titleCard, { autoAlpha: 0, duration: 0.6, ease: "power2.in" }, plans[0].start - 0.5);
  tl.fromTo(viewport, { height: H }, { height: STAGE, duration: CAMERA, ease: "power2.inOut" }, plans[0].start);

  const path = [];
  let from = ENTRY;
  plans.forEach((plan, i) => {
    tl.to(camera, { ...shots[i], duration: CAMERA, ease: "power2.inOut" }, plan.start);
    tl.fromTo(plan.heading, { autoAlpha: 0 }, { autoAlpha: 1, duration: 0.5 }, plan.start + 0.6);
    tl.to(plan.heading, { autoAlpha: 0, duration: 0.4 }, plan.end - 0.4);
    if (i === 0) tl.fromTo(cursor, { opacity: 0 }, { opacity: 1, duration: 0.4 }, plan.start + CAMERA - 0.4);

    // The cursor rests where it was while the camera moves to the next part of the page.
    path.push({ start: plan.start, end: plan.start + CAMERA, from, to: from });
    for (const beat of plan.beats) {
      tl.fromTo(beat.note, { autoAlpha: 0, y: 12 }, { autoAlpha: 1, y: 0, duration: 0.5, ease: "power2.out" }, beat.arrive - 0.4);
      tl.to(beat.note, { autoAlpha: 0, duration: 0.3, ease: "power2.in" }, beat.end - 0.3);

      const first = beat.points[0];
      // Between two points on one chart the cursor keeps pointing at it, so its tooltip stays up.
      path.push({ start: beat.travelStart, end: beat.arrive, from, to: first, hover: first.act ? false : "same" });
      const legs = beat.points.length - 1;
      for (let k = 0; k < legs; k++) {
        const span = (beat.scrubEnd - beat.arrive) / legs;
        path.push({ start: beat.arrive + k * span, end: beat.arrive + (k + 1) * span, from: beat.points[k], to: beat.points[k + 1], hover: true, ease: "sine.inOut" });
      }
      from = beat.points[legs];
      let restFrom = legs ? beat.scrubEnd : beat.arrive;
      // Between actions the cursor moves to the next one's target, arriving as it runs.
      for (const action of beat.actions.slice(1)) {
        const next = { anchor: action.target, fx: 0.5, fy: 0.5, act: true, action };
        const leave = Math.max(restFrom, action.t - 0.35);
        path.push({ start: restFrom, end: leave, from, to: from, hover: !from.act });
        path.push({ start: leave, end: action.t, from, to: next });
        from = next;
        restFrom = action.t;
      }
      path.push({ start: restFrom, end: beat.end, from, to: from, hover: !from.act });
    }
  });
  path.push({ start: timing.outro, end: timing.total, from, to: from });

  tl.to(cursor, { opacity: 0, duration: 0.3 }, timing.outro);
  tl.to(viewport, { height: H, duration: CAMERA, ease: "power2.inOut" }, timing.outro);
  tl.to(camera, { ...overview, duration: CAMERA, ease: "power2.inOut" }, timing.outro);
  tl.to(watermark, { autoAlpha: 0, duration: 0.5 }, timing.outro + LOGO_IN - 0.6);
  tl.fromTo(end, { autoAlpha: 0 }, { autoAlpha: 1, duration: 0.6, ease: "power2.out" }, timing.outro + LOGO_IN - 0.6);
  // A nested timeline is driven by its parent only while it is not paused itself.
  tl.add(logo.tl.paused(false), timing.outro + LOGO_IN);

  // A seek that changed the page waits, before its frame is taken, for the page to settle and the
  // cursor to point again.
  let replacing = null;
  window.addEventListener("hf-seek", (event) => {
    if (replacing) event.detail.waitUntil(replacing);
  });
  tl.eventCallback("onUpdate", () => place(tl.time()));
  place(0);
  filmReady = true;
  return { timeline: tl };

  /**
   * Puts the page in its state at `t` and the cursor where the path has it. Between two points on
   * one chart the cursor moves in the chart's own space and points at the chart as it goes;
   * otherwise it travels in screen space, so a point on the page follows the camera.
   */
  function place(t, again = false) {
    const changed = stateAt(t);
    // Focus and the browser may scroll a clipped box; the camera alone decides what is in frame.
    for (const node of [viewport, root, document.scrollingElement]) {
      if (node && (node.scrollTop || node.scrollLeft)) node.scrollTop = node.scrollLeft = 0;
    }
    // A page answers an action (or a remount) over several turns, a heavy chart over many: once it
    // has settled, point again, so the frame shows the page as it now is.
    if (changed && !again) {
      lastPoint = null;
      const settled = (replacing = (async () => {
        await settle(document.body);
        // The rest of the actions this seek landed on, each once the page has answered the last.
        while (replacing === settled && behind(tl.time())) {
          stateAt(tl.time());
          await settle(document.body);
        }
      })().then(() => {
        if (replacing !== settled) return;
        replacing = null;
        prepareForAudit(target);
        lastPoint = null;
        place(tl.time(), true);
      }));
    }
    const segment = path.findLast((s) => t >= s.start) ?? path[0];
    const p = Math.min(1, Math.max(0, (t - segment.start) / (segment.end - segment.start)));
    const k = window.gsap.parseEase(segment.ease ?? "power2.inOut")(p);
    let at;
    const a = segment.hover ? local(segment.from) : null;
    const b = segment.hover ? local(segment.to) : null;
    if (segment.hover && (segment.hover !== "same" || (a && b && a.el === b.el))) {
      const point = a && b ? { el: a.el, x: a.x + (b.x - a.x) * k, y: a.y + (b.y - a.y) * k } : a;
      hover(point);
      at = point ? screenOf(point) : spot(segment.from);
    } else {
      hover(null);
      const start = spot(segment.from);
      const finish = spot(segment.to);
      at = { x: start.x + (finish.x - start.x) * k, y: start.y + (finish.y - start.y) * k };
    }
    const rootBox = root.getBoundingClientRect();
    const rootScale = rootBox.width / W;
    window.gsap.set(cursor, { x: (at.x - rootBox.left) / rootScale - 6, y: (at.y - rootBox.top) / rootScale - 3 });
  }

  /**
   * Where a point is on screen. An action's target is where the settled rehearsal saw it: menus and
   * popovers are placed asynchronously, so within the seek that opens one its live position is
   * wrong, and the camera holds still within a scene, so the rehearsed spot is where it is.
   */
  function spot(point) {
    if (point.screen) return { x: point.screen[0], y: point.screen[1] };
    const live = point.action?.fallback ? null : local(point);
    if (live) return screenOf(live);
    const seen = point.action?.fallback ?? { x: ENTRY.screen[0], y: ENTRY.screen[1] };
    const rootBox = root.getBoundingClientRect();
    const rootScale = rootBox.width / W;
    return { x: rootBox.left + seen.x * rootScale, y: rootBox.top + seen.y * rootScale };
  }

  /**
   * Scrolls an action's target that sits in a scrolling overlay (a dialog taller than the space
   * above the caption band) to the middle of the part of it in frame, as a person scrolls to a
   * field before using it. The page never scrolls: the camera frames it. Runs when the target is
   * first looked up in a mount, so a frame's scroll follows from its time alone.
   */
  function reveal(node) {
    if (!node || target.contains(node)) return node;
    for (let box = node.parentElement; box && box !== document.body; box = box.parentElement) {
      if (!/auto|scroll/.test(getComputedStyle(box).overflowY) || box.scrollHeight <= box.clientHeight) continue;
      const rootBox = root.getBoundingClientRect();
      const b = box.getBoundingClientRect();
      const r = node.getBoundingClientRect();
      const bottom = Math.min(b.bottom, rootBox.top + (STAGE * rootBox.width) / W);
      box.scrollTop += r.top + r.height / 2 - (b.top + bottom) / 2;
      break;
    }
    return node;
  }

  /** The middle of `node` now, in the composition's own pixels. */
  function rehearsedSpot(node) {
    if (!node) return null;
    const rootBox = root.getBoundingClientRect();
    const rootScale = rootBox.width / W;
    const r = node.getBoundingClientRect();
    return { x: (r.left + r.width / 2 - rootBox.left) / rootScale, y: (r.top + r.height / 2 - rootBox.top) / rootScale };
  }

  function hover(point) {
    if (point && lastPoint && point.el === lastPoint.el && point.x === lastPoint.x && point.y === lastPoint.y) return;
    lastPoint = point;
    if (hovered) delete hovered.dataset.tourHover;
    // A chart keeps the point last hovered until the pointer leaves it, and one whose data then
    // changes draws that point's highlight from a scale that no longer holds it.
    if (left && left.isConnected && hovered !== point?.el) {
      for (let node = left; node && node !== document.body; node = node.parentElement) {
        node.dispatchEvent(new PointerEvent("pointerleave", { bubbles: false, pointerType: "mouse" }));
      }
      flushSync();
    }
    if (hovered !== point?.el) left = null;
    hovered = point?.el ?? null;
    if (!point) return;
    const node = point.el;
    node.dataset.tourHover = "";
    const rect = node.getBoundingClientRect();
    const visual = screenOf(point);
    // A dialog's scroll lock leaves the body unpointable until a timer the render never runs.
    const locked = document.body.style.pointerEvents;
    document.body.style.pointerEvents = "";
    const hit = document.elementsFromPoint(visual.x, visual.y).find((n) => node.contains(n) && n !== node) ?? node;
    document.body.style.pointerEvents = locked;
    left = hit;
    // layerchart reads clientX - rect.left without undoing CSS scale, so the event carries the
    // unscaled offset while the hit test uses where the point actually is on screen.
    hit.dispatchEvent(
      new PointerEvent("pointermove", { clientX: rect.left + point.x, clientY: rect.top + point.y, bubbles: true, pointerType: "mouse" })
    );
    flushSync();
  }
}

/**
 * Mounts the page into `target` as the film shows it, with no film: for finding card titles and
 * selectors while writing one. Returns the mounted component.
 */
export function preview(target) {
  page.url = new URL(fixture.route, "http://localhost");
  // The viewer is the tenant's owner, as when the fixture was captured: pages list only what the
  // viewer's permissions reach.
  Object.assign(page.data, { user: { name: "Demo" }, effectivePermissions: ["*"] });
  useFixture(fixture);
  target.dataset.tour = "";
  const component = mount(Host, { target });
  flushSync();
  return component;
}

/**
 * Waits until the page stops changing. A page that loads in `onMount`, awaiting its queries one
 * after another, fills in over several turns of the event loop, and everything `film` measures has
 * to be there first. Capped, so a page that never stops changing still films.
 */
async function settle(target) {
  let quiet = 0;
  const observer = new MutationObserver(() => {
    quiet = 0;
  });
  observer.observe(target, { childList: true, subtree: true, characterData: true });
  for (let turn = 0; turn < 200 && quiet < 3; turn++) {
    await new Promise((resolve) => setTimeout(resolve, 0));
    flushSync();
    quiet++;
  }
  observer.disconnect();
}

/**
 * Marks what the layout audit misreads. A closed <details> still lays out its hidden content,
 * which it reads as overlap. Page text is covered only by the tour's own cards, and below the
 * stage line it is clipped by the viewport, which it does not model: it reads clipped text under
 * the band as occluded.
 */
function prepareForAudit(target) {
  for (const node of target.querySelectorAll("details:not([open]) > :not(summary)")) node.dataset.layoutIgnore = "";
  for (const node of target.querySelectorAll("*")) {
    if ([...node.childNodes].some((child) => child.nodeType === Node.TEXT_NODE && child.textContent.trim())) {
      node.dataset.layoutAllowOcclusion = "";
    }
  }
}

/** A beat's points, each an anchor lookup and where on it: fractions of its size, or an element in it. */
function pointsOf(beat, anchor) {
  if (beat.element && !beat.at && !beat.path) return [{ anchor, fx: 0.5, fy: 0.5 }];
  if (beat.on) return [{ anchor, on: beat.on }];
  return (beat.path ?? [beat.at]).map(([fx, fy]) => ({ anchor, fx, fy }));
}

/** A point in its anchor element's own unscaled pixels, or null while the element is missing. */
function local(point) {
  if (point.screen) return null;
  const node = point.anchor();
  if (!node) return null;
  if (point.on) {
    const target = node.querySelector(point.on);
    if (!target) return null;
    const c = node.getBoundingClientRect();
    const r = target.getBoundingClientRect();
    const scale = c.width / node.offsetWidth || 1;
    return { el: node, x: (r.left + r.width / 2 - c.left) / scale, y: (r.top + r.height / 2 - c.top) / scale };
  }
  return { el: node, x: point.fx * node.offsetWidth, y: point.fy * node.offsetHeight };
}

function screenOf({ el: node, x, y }) {
  const rect = node.getBoundingClientRect();
  const scale = rect.width / node.offsetWidth || 1;
  return { x: rect.left + x * scale, y: rect.top + y * scale };
}

/** The first visible element `spec`'s selector matches anywhere in the document, narrowed by `spec.text`. */
function find(spec) {
  const selector = spec.click ?? spec.type ?? spec.choose;
  const visible = (node) => node.getClientRects().length > 0;
  const matches = [...document.querySelectorAll(selector)].filter(visible);
  return spec.text !== undefined && spec.type === undefined
    ? (matches.find((node) => node.textContent.trim() === spec.text) ?? matches.find((node) => node.textContent.includes(spec.text)) ?? null)
    : (matches[0] ?? null);
}

/** A real click as the app's handlers see it: pointer and mouse down, focus, up, then click. */
function press(node) {
  const rect = node.getBoundingClientRect();
  // `detail` is the click count a real mouse reports; a 0 reads as keyboard activation, which bits-ui
  // menus answer by toggling a second time.
  const at = { bubbles: true, cancelable: true, clientX: rect.left + rect.width / 2, clientY: rect.top + rect.height / 2, button: 0, detail: 1 };
  node.dispatchEvent(new PointerEvent("pointerdown", { ...at, pointerType: "mouse", isPrimary: true }));
  node.dispatchEvent(new MouseEvent("mousedown", at));
  node.focus?.({ preventScroll: true });
  node.dispatchEvent(new PointerEvent("pointerup", { ...at, pointerType: "mouse", isPrimary: true }));
  node.dispatchEvent(new MouseEvent("mouseup", at));
  node.dispatchEvent(new MouseEvent("click", at));
}

function el(tag, className, parent) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  parent.append(node);
  return node;
}

/** Fills the title card with a heading and a line, and returns the block that holds them. */
function fill(cardEl, heading, text) {
  const block = el("div", "", cardEl);
  const h = el("h1", "", block);
  h.textContent = heading;
  h.dataset.layoutAllowOverlap = "";
  if (text) {
    const p = el("p", "", block);
    p.textContent = text;
    p.dataset.layoutAllowOverlap = "";
  }
  return block;
}

function card(within, title) {
  const titles = [...within.querySelectorAll('[data-slot="card-title"]')];
  const found = titles.find((node) => node.textContent.trim().startsWith(title));
  return found?.closest('[data-slot="card"]') ?? null;
}

/** Camera transform that centres the elements, `margin` around them, on `centreY` and fills `fill` of the frame. */
function shot(elements, fillFraction, centreY, margin = 0) {
  const boxes = elements.map((node) => node.getBoundingClientRect());
  const x = Math.min(...boxes.map((b) => b.left)) - margin;
  const y = Math.min(...boxes.map((b) => b.top)) - margin;
  const w = Math.max(...boxes.map((b) => b.right)) + margin - x;
  const h = Math.max(...boxes.map((b) => b.bottom)) + margin - y;
  const scale = Math.min((W * fillFraction) / w, (H * fillFraction) / h);
  return { x: W / 2 - scale * (x + w / 2), y: centreY - scale * (y + h / 2), scale };
}
