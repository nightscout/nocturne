<script lang="ts">
  import {
    computePosition,
    autoUpdate,
    offset,
    flip,
    shift,
    arrow as arrowMiddleware,
  } from "@floating-ui/dom";
  import { getCoachMarkContext } from "../context.svelte.js";
  import StepControls from "./StepControls.svelte";

  const ctx = getCoachMarkContext();

  let popoverEl: HTMLElement | undefined = $state();
  let arrowEl: HTMLElement | undefined = $state();
  let currentLocalStep = $state(0);
  let dwellTimer: ReturnType<typeof setTimeout> | null = null;
  let cleanupAutoUpdate: (() => void) | null = null;

  let { navigationFlag = { navigating: false } }: { navigationFlag?: { navigating: boolean } } = $props();

  let historyEntryPushed = false;
  let dismissedByUI = false;

  const SPOTLIGHT_PADDING = 8;

  let spotlightClipPath = $state("");

  // Mobile renders the popover as a bottom sheet anchored by CSS. Track the
  // breakpoint so we can suppress floating-ui's inline positioning there.
  let isMobile = $state(false);

  $effect(() => {
    if (typeof window === "undefined") return;
    const mq = window.matchMedia("(max-width: 640px)");
    isMobile = mq.matches;
    const onChange = (e: MediaQueryListEvent) => (isMobile = e.matches);
    mq.addEventListener("change", onChange);
    return () => mq.removeEventListener("change", onChange);
  });

  const activeKey = $derived(ctx.activeKey);
  const mountedSteps = $derived(activeKey ? ctx.getMountedSteps(activeKey) : []);
  const totalLocalSteps = $derived(mountedSteps.length);
  // The reset of `currentLocalStep` below lands after the template has read it,
  // so a mark left on a later local step would hand over to one with fewer and
  // unmount the overlay for a frame. Clamping keeps it up.
  const localStep = $derived(Math.min(currentLocalStep, Math.max(totalLocalSteps - 1, 0)));
  const currentRegistration = $derived(mountedSteps[localStep] ?? null);

  // Reset local step when active mark changes
  $effect(() => {
    if (activeKey) {
      currentLocalStep = 0;
      startDwellTimer();
    } else {
      cancelDwellTimer();
    }
  });

  // History management for back-button dismissal.
  // Push a sentinel entry when the overlay appears; pop it when it disappears.
  // The effect tracks whether an overlay is up, never which mark is up: keying
  // it on `activeKey` tore the sentinel down on every step of a sequence, and
  // the teardown's own `ctx.activeKey` read cannot tell a step change from a
  // dismissal — a derived read outside a reaction sees the batch's earlier
  // value, which for a step change is the momentary null between the two marks.
  const overlayVisible = $derived(activeKey !== null);

  $effect(() => {
    if (!overlayVisible) return;

    if (!historyEntryPushed) {
      dismissedByUI = false; // reset stale flag from any previous cycle
      history.pushState({ ...history.state, __coachMark: true }, "");
      historyEntryPushed = true;
    }

    function onPopState() {
      // Guard: if the UI already dismissed (Escape/backdrop/button),
      // this popstate is just the history.back() cleanup — ignore it.
      if (dismissedByUI) {
        dismissedByUI = false;
        return;
      }

      // The user pressed back. Dismiss with quiet so no follow-on sequence appears.
      historyEntryPushed = false;
      const key = ctx.activeKey;
      if (key) ctx.dismiss(key, { quiet: true });
    }

    window.addEventListener("popstate", onPopState);

    return () => {
      window.removeEventListener("popstate", onPopState);

      if (!historyEntryPushed) return;
      historyEntryPushed = false;

      if (navigationFlag.navigating) {
        // SvelteKit is navigating — don't call history.back() which
        // would fight the router. Replace the current state to strip
        // our marker (the router's pushState has already happened).
        navigationFlag.navigating = false;
        const cleaned = { ...history.state };
        delete cleaned.__coachMark;
        history.replaceState(cleaned, "");
      } else {
        // Natural dismiss (Escape, backdrop, "Got it") — pop our entry.
        dismissedByUI = true;
        history.back();
      }
    };
  });

  function updateSpotlightRect(element: Element) {
    const rect = element.getBoundingClientRect();
    const pad = SPOTLIGHT_PADDING;
    const top = rect.top - pad;
    const left = rect.left - pad;
    const bottom = rect.bottom + pad;
    const right = rect.right + pad;
    const r = parseFloat(getComputedStyle(element).borderRadius || "0") + pad;

    // Outer rect (full viewport) clockwise, inner rounded rect counter-clockwise
    // Using polygon with evenodd for the cutout; approximate rounded corners with extra points
    if (r > 0) {
      spotlightClipPath = `polygon(evenodd,
        0% 0%, 100% 0%, 100% 100%, 0% 100%, 0% 0%,
        ${left + r}px ${top}px,
        ${right - r}px ${top}px,
        ${right}px ${top + r}px,
        ${right}px ${bottom - r}px,
        ${right - r}px ${bottom}px,
        ${left + r}px ${bottom}px,
        ${left}px ${bottom - r}px,
        ${left}px ${top + r}px,
        ${left + r}px ${top}px
      )`;
    } else {
      spotlightClipPath = `polygon(evenodd,
        0% 0%, 100% 0%, 100% 100%, 0% 100%, 0% 0%,
        ${left}px ${top}px,
        ${left}px ${bottom}px,
        ${right}px ${bottom}px,
        ${right}px ${top}px,
        ${left}px ${top}px
      )`;
    }
  }

  // Position popover — scroll the popover tooltip into view, not the target element.
  // For large target elements, scrollIntoView on the element itself can push the
  // tooltip off-screen because the browser centers the (potentially huge) element.
  $effect(() => {
    if (currentRegistration && popoverEl) {
      // Initial scroll: use "nearest" so the browser only scrolls if the element
      // is fully off-screen, avoiding jarring jumps for large elements.
      currentRegistration.element.scrollIntoView({ behavior: "smooth", block: "nearest" });

      cleanupAutoUpdate?.();
      let initialPosition = true;
      cleanupAutoUpdate = autoUpdate(currentRegistration.element, popoverEl, () => {
        if (!currentRegistration || !popoverEl) return;

        updateSpotlightRect(currentRegistration.element);

        // On mobile the popover is a bottom sheet positioned entirely by CSS.
        // Applying floating-ui's inline top/left would override the CSS top:auto
        // and fight bottom:0, stretching the sheet to fill the viewport.
        if (isMobile) {
          popoverEl.style.left = "";
          popoverEl.style.top = "";
          return;
        }

        computePosition(currentRegistration.element, popoverEl, {
          strategy: "fixed",
          placement: "bottom",
          middleware: [
            offset(12 + SPOTLIGHT_PADDING),
            flip(),
            shift({ padding: 8 }),
            ...(arrowEl ? [arrowMiddleware({ element: arrowEl })] : []),
          ],
        }).then(({ x, y, middlewareData }) => {
          if (!popoverEl) return;
          Object.assign(popoverEl.style, { left: `${x}px`, top: `${y}px` });
          if (arrowEl && middlewareData.arrow) {
            Object.assign(arrowEl.style, {
              left: middlewareData.arrow.x != null ? `${middlewareData.arrow.x}px` : "",
              top: middlewareData.arrow.y != null ? `${middlewareData.arrow.y}px` : "",
            });
          }

          // After the first position computation, scroll the popover itself into
          // view so the user can always see the tooltip — even when the target
          // element is taller than the viewport.
          if (initialPosition) {
            initialPosition = false;
            popoverEl.scrollIntoView({ behavior: "smooth", block: "nearest" });
          }
        });
      });
    }
    return () => {
      cleanupAutoUpdate?.();
    };
  });

  function startDwellTimer() {
    cancelDwellTimer();
    dwellTimer = setTimeout(() => {
      if (activeKey) ctx.markSeen(activeKey);
    }, ctx.seenDwell);
  }

  function cancelDwellTimer() {
    if (dwellTimer) {
      clearTimeout(dwellTimer);
      dwellTimer = null;
    }
  }

  function handleDismiss() {
    if (activeKey) ctx.dismiss(activeKey);
  }
  function handleComplete() {
    if (activeKey) ctx.complete(activeKey);
  }
  function handleBack() {
    if (localStep > 0) currentLocalStep = localStep - 1;
  }
  function handleNext() {
    if (localStep < totalLocalSteps - 1) currentLocalStep = localStep + 1;
  }

  function handleKeydown(e: KeyboardEvent) {
    if (e.key === "Escape") handleDismiss();
    else if (e.key === "ArrowLeft") handleBack();
    else if (e.key === "ArrowRight") handleNext();
  }

  $effect(() => {
    if (popoverEl && activeKey) popoverEl.focus();
  });
</script>

{#if activeKey && currentRegistration}
  <!-- svelte-ignore a11y_no_static_element_interactions -->
  <div
    class="coach-backdrop"
    data-testid="coach-backdrop"
    style:clip-path={spotlightClipPath}
    onkeydown={handleKeydown}
    onclick={handleDismiss}
  ></div>
  <div
    bind:this={popoverEl}
    class="coach-popover"
    role="dialog"
    aria-label={currentRegistration.title}
    aria-live="polite"
    tabindex="-1"
    onkeydown={handleKeydown}
  >
    <div class="coach-popover__arrow" bind:this={arrowEl}></div>
    <button
      type="button"
      class="coach-popover__close"
      aria-label="Dismiss"
      onclick={handleDismiss}>&times;</button
    >
    <h3 class="coach-popover__title">{currentRegistration.title}</h3>
    <p class="coach-popover__description">{currentRegistration.description}</p>
    <StepControls
      currentStep={localStep}
      totalSteps={totalLocalSteps}
      action={currentRegistration.action}
      onback={handleBack}
      onnext={handleNext}
      oncomplete={handleComplete}
    />
  </div>
{/if}
