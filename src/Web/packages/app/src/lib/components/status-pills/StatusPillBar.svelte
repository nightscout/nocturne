<script lang="ts">
  import IOBPill from "./IOBPill.svelte";
  import COBPill from "./COBPill.svelte";
  import CAGEPill from "./CAGEPill.svelte";
  import SAGEPill from "./SAGEPill.svelte";
  import BasalPill from "./BasalPill.svelte";
  import LoopPill from "./LoopPill.svelte";
  import type {
    IOBPillData,
    COBPillData,
    CAGEPillData,
    SAGEPillData,
    BasalPillData,
    LoopPillData,
    StatusPillsConfig,
  } from "$lib/types/status-pills";
  import { cn } from "$lib/utils";

  interface StatusPillBarProps {
    /** IOB data */
    iob?: IOBPillData | null;
    /** COB data */
    cob?: COBPillData | null;
    /** CAGE data */
    cage?: CAGEPillData | null;
    /** SAGE data */
    sage?: SAGEPillData | null;
    /** Basal data */
    basal?: BasalPillData | null;
    /** Loop data */
    loop?: LoopPillData | null;
    /** Configuration for pills */
    config?: Partial<StatusPillsConfig>;
    /** BG units for display */
    units?: string;
    /** Additional CSS classes */
    class?: string;
  }

  let {
    iob = null,
    cob = null,
    cage = null,
    sage = null,
    basal = null,
    loop = null,
    config = {},
    units = "mmol/L",
    class: className,
  }: StatusPillBarProps = $props();

  // Merge with defaults
  const mergedConfig = $derived({
    enabledPills: config.enabledPills ?? [
      "iob",
      "cob",
      "cage",
      "sage",
      "basal",
      "loop",
    ],
    cage: { ...{ urgentThreshold: 72 }, ...config.cage },
    sage: { ...{ urgentThreshold: 240 }, ...config.sage },
  });

  // Check which pills should be shown
  const showIob = $derived(mergedConfig.enabledPills.includes("iob"));
  const showCob = $derived(mergedConfig.enabledPills.includes("cob"));
  const showCage = $derived(mergedConfig.enabledPills.includes("cage"));
  const showSage = $derived(mergedConfig.enabledPills.includes("sage"));
  const showBasal = $derived(mergedConfig.enabledPills.includes("basal"));
  const showLoop = $derived(mergedConfig.enabledPills.includes("loop"));

  // Compute whether we have any data to show
  const hasAnyData = $derived(
    (showIob && iob) ||
      (showCob && cob) ||
      (showCage && cage) ||
      (showSage && sage) ||
      (showBasal && basal) ||
      (showLoop && loop)
  );
</script>

{#if hasAnyData}
  <div class={cn("flex flex-wrap items-center gap-2", className)}>
    {#if showLoop && loop}
      <LoopPill data={loop} {units} />
    {/if}

    {#if showIob && iob}
      <IOBPill data={iob} {units} />
    {/if}

    {#if showCob && cob}
      <COBPill data={cob} />
    {/if}

    {#if showCage && cage}
      <CAGEPill data={cage} maxLifeHours={mergedConfig.cage.urgentThreshold} />
    {/if}

    {#if showSage && sage}
      <SAGEPill data={sage} maxLifeHours={mergedConfig.sage.urgentThreshold} />
    {/if}

    {#if showBasal && basal}
      <BasalPill data={basal} />
    {/if}
  </div>
{/if}
