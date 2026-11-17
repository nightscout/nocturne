<script lang="ts">
  import { getCoachMarkContext } from "../context.svelte.js";

  const ctx = getCoachMarkContext();
  let {
    currentStep,
    totalSteps,
    action,
    onback,
    onnext,
    oncomplete,
  }: {
    currentStep: number;
    totalSteps: number;
    action?: { label: string; href: string } | undefined;
    onback: () => void;
    onnext: () => void;
    oncomplete: () => void;
  } = $props();

  const isFirst = $derived(currentStep === 0);
  const isLast = $derived(currentStep === totalSteps - 1);
</script>

<div class="coach-step-controls">
  {#if totalSteps > 1}
    <span class="coach-step-indicator">{ctx.labels.stepCounter(currentStep + 1, totalSteps)}</span>
  {/if}

  <div class="coach-step-buttons">
    {#if !isFirst}
      <button type="button" class="coach-btn coach-btn--ghost" onclick={onback}>{ctx.labels.back}</button>
    {/if}

    {#if isLast && action}
      <a href={action.href} class="coach-btn coach-btn--primary" onclick={oncomplete}
        >{action.label}</a
      >
    {:else if !isLast}
      <button type="button" class="coach-btn coach-btn--primary" onclick={onnext}>{ctx.labels.next}</button>
    {:else}
      <button type="button" class="coach-btn coach-btn--primary" onclick={oncomplete}>{ctx.labels.gotIt}</button>
    {/if}
  </div>
</div>
