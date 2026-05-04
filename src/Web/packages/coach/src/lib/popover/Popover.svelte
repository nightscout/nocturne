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

  const SPOTLIGHT_PADDING = 8;

  let spotlightClipPath = $state("");

  const activeKey = $derived(ctx.activeKey);
  const mountedSteps = $derived(activeKey ? ctx.getMountedSteps(activeKey) : []);
  const currentRegistration = $derived(mountedSteps[currentLocalStep] ?? null);
  const totalLocalSteps = $derived(mountedSteps.length);

  // Reset local step when active mark changes
  $effect(() => {
    if (activeKey) {
      currentLocalStep = 0;
      startDwellTimer();
    } else {
      cancelDwellTimer();
    }
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

  // Position popover
  $effect(() => {
    if (currentRegistration && popoverEl) {
      currentRegistration.element.scrollIntoView({ behavior: "smooth", block: "center" });

      cleanupAutoUpdate?.();
      cleanupAutoUpdate = autoUpdate(currentRegistration.element, popoverEl, () => {
        if (!currentRegistration || !popoverEl) return;

        updateSpotlightRect(currentRegistration.element);

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
  function handleBack() {
    if (currentLocalStep > 0) currentLocalStep--;
  }
  function handleNext() {
    if (currentLocalStep < totalLocalSteps - 1) currentLocalStep++;
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
      currentStep={currentLocalStep}
      totalSteps={totalLocalSteps}
      action={currentRegistration.action}
      onback={handleBack}
      onnext={handleNext}
      ondismiss={handleDismiss}
    />
  </div>
{/if}
