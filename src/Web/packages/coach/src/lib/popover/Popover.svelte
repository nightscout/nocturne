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
  import type { HistorySentinel } from "../history-sentinel.js";
  import StepControls from "./StepControls.svelte";

  const ctx = getCoachMarkContext();

  let popoverEl: HTMLElement | undefined = $state();
  let arrowEl: HTMLElement | undefined = $state();
  let currentLocalStep = $state(0);
  let dwellTimer: ReturnType<typeof setTimeout> | null = null;
  let cleanupAutoUpdate: (() => void) | null = null;

  let { sentinel }: { sentinel: HistorySentinel } = $props();

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

  // The overlay waits for its target to come on screen instead of scrolling the page to it, and
  // once raised stays up while the reader scrolls. The synchronous rect check lets consecutive
  // marks that are both on screen hand over without the overlay unmounting between them.
  const target = $derived(currentRegistration?.element ?? null);
  let revealedTarget: HTMLElement | null = $state(null);
  const revealed = $derived(target !== null && (revealedTarget === target || isOnScreen(target)));

  $effect(() => {
    const el = target;
    if (!el) return;
    const observer = new IntersectionObserver((entries) => {
      if (entries.some((entry) => entry.isIntersecting)) {
        revealedTarget = el;
        observer.disconnect();
      }
    });
    observer.observe(el);
    return () => observer.disconnect();
  });

  function isOnScreen(el: HTMLElement): boolean {
    const rect = el.getBoundingClientRect();
    return (
      (rect.width > 0 || rect.height > 0) &&
      rect.bottom > 0 &&
      rect.right > 0 &&
      rect.top < window.innerHeight &&
      rect.left < window.innerWidth
    );
  }

  $effect(() => {
    if (activeKey) currentLocalStep = 0;
  });

  // Time spent on a mark counts from when it can be read.
  $effect(() => {
    if (activeKey && revealed) startDwellTimer();
    else cancelDwellTimer();
  });

  // The history entry tracks whether an overlay is up, never which mark is up: keying it on
  // `activeKey` tore the entry down on every step of a sequence.
  const overlayVisible = $derived(activeKey !== null && revealed);

  $effect(() => {
    if (!overlayVisible) return;
    sentinel.push();
    return () => sentinel.release();
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

  $effect(() => {
    if (currentRegistration && popoverEl) {
      cleanupAutoUpdate?.();
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
            // Nothing scrolls the popover into view, so a target taller than the viewport must not
            // carry it off screen.
            shift({ padding: 8, crossAxis: true }),
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
    if (popoverEl && activeKey) popoverEl.focus({ preventScroll: true });
  });
</script>

{#if activeKey && currentRegistration && revealed}
  <!-- svelte-ignore a11y_no_static_element_interactions -->
  <div
    class="coach-backdrop"
    data-testid="coach-backdrop"
    style:--coach-spotlight={spotlightClipPath || null}
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
