<script lang="ts">
  import { formatLocale } from "$lib/utils/formatting";
  import * as Popover from "$lib/components/ui/popover";
  import { Button } from "$lib/components/ui/button";
  import type { TrackerInstanceDto, TrackerDefinitionDto } from "$lib/api";
  import { TrackerCategory } from "$lib/api";
  import { cn } from "$lib/utils";
  import Check from "@lucide/svelte/icons/check";
  import Clock from "@lucide/svelte/icons/clock";
  import TriangleAlert from "@lucide/svelte/icons/triangle-alert";
  import { TrackerCategoryIcon } from "$lib/components/icons";
  import { reachedUrgency } from "$lib/components/trackers/schedule";
  import {
    ageHoursSince,
    formatAge,
    formatLifespan,
  } from "$lib/components/trackers/age.svelte";
  import {
    PROGRESS_FILL,
    VALUE_TONE,
    alertLevel,
    alertLevelPrefix,
    trackerPillClasses,
  } from "./tracker-pill-style.svelte";

  interface TrackerPillProps {
    /** The tracker instance to display */
    instance: TrackerInstanceDto;
    /** The tracker definition for metadata */
    definition?: TrackerDefinitionDto;
    /** Epoch milliseconds the reached level is judged against. */
    now: number;
    /** Additional CSS classes */
    class?: string;
    /** Callback when complete button is clicked */
    onComplete?: (
      instanceId: string,
      instanceName: string,
      category: TrackerCategory,
      definitionId: string,
      completionEventType?: string
    ) => void;
  }

  let {
    instance,
    definition,
    now,
    class: className,
    onComplete,
  }: TrackerPillProps = $props();

  let popoverOpen = $state(false);

  const ageHours = $derived(ageHoursSince(instance.startedAt, now) ?? instance.ageHours);

  const timeRemaining = $derived.by(() => {
    if (ageHours === undefined || ageHours === null || !definition?.lifespanHours) return undefined;
    return definition.lifespanHours - ageHours;
  });

  // Capped at 100%.
  const progressPercent = $derived.by(() => {
    if (ageHours === undefined || ageHours === null || !definition?.lifespanHours) return 0;
    return Math.min(100, (ageHours / definition.lifespanHours) * 100);
  });

  // Only show progress bar if over 10%
  const showProgress = $derived(progressPercent > 10);

  // Format time remaining
  function formatTimeRemaining(hours: number | undefined): string {
    if (hours === undefined) return "Unknown";
    if (hours <= 0) return "Overdue";
    return formatAge(hours);
  }

  const level = $derived(alertLevel(reachedUrgency(instance, now)));
  const alertPrefix = $derived(alertLevelPrefix(level));

  // eslint-disable-next-line shadcn/require-static-classes -- this is the pill component, and PopoverTrigger renders unstyled.
  const pillClasses = $derived(trackerPillClasses(className));

  const label = $derived(
    instance.definitionName ?? definition?.name ?? "Tracker"
  );
  const ageDisplay = $derived(formatAge(ageHours));

  function handleComplete() {
    popoverOpen = false;
    onComplete?.(
      instance.id!,
      label,
      instance.category ?? definition?.category ?? TrackerCategory.Custom,
      (instance.definitionId ?? definition?.id ?? "").toString(),
      definition?.completionEventType
    );
  }
</script>

<Popover.Root bind:open={popoverOpen}>
  <Popover.Trigger class={pillClasses} data-pill-trigger>
    {#if showProgress}
      <span
        class="absolute bottom-0 left-3 h-px {PROGRESS_FILL[level]} w-[calc((100%-1.5rem)*var(--progress))] transition-all duration-500 ease-out"
        style:--progress={progressPercent / 100}
        aria-hidden="true"
      ></span>
    {/if}
    <span class="flex items-center gap-1 text-xs text-muted-foreground">
      <TrackerCategoryIcon
        category={definition?.category ?? TrackerCategory.Custom}
        class="size-3"
      />
      {label}
    </span>
    <span class="flex items-center gap-1 text-sm font-medium tabular-nums {VALUE_TONE[level]}">
      {#if alertPrefix}
        <TriangleAlert class="size-3.5" aria-hidden="true" />
        <span class="sr-only">{alertPrefix}</span>
      {/if}
      {ageDisplay}
    </span>
  </Popover.Trigger>
  <Popover.Content
    class="w-72 p-0"
    align="center"
    side="bottom"
    data-testid="tracker-pill-popover"
  >
    <div class="px-4 py-3 border-b border-border">
      <h4 class="font-semibold text-sm flex items-center gap-2">
        <TrackerCategoryIcon
          category={definition?.category ?? TrackerCategory.Custom}
          class="h-4 w-4"
        />
        {label}
      </h4>
      <p class="text-xs text-muted-foreground">Active tracker</p>
    </div>
    <div class="px-4 py-3 space-y-2">
      <div class="flex justify-between items-center text-sm">
        <span class="text-muted-foreground flex items-center gap-1.5">
          <Clock class="h-3.5 w-3.5" />
          Running time
        </span>
        <span class="font-medium">{ageDisplay}</span>
      </div>
      {#if definition?.lifespanHours}
        <div class="flex justify-between items-center text-sm">
          <span class="text-muted-foreground">Expected lifespan</span>
          <span class="font-medium">
            {formatLifespan(definition.lifespanHours)}
          </span>
        </div>
        <div class="flex justify-between items-center text-sm">
          <span class="text-muted-foreground">Time remaining</span>
          <span
            class={cn(
              "font-medium",
              timeRemaining !== undefined && timeRemaining <= 0
                ? "text-destructive"
                : timeRemaining !== undefined && timeRemaining < 6
                  ? "text-severity-warn"
                  : ""
            )}
          >
            {formatTimeRemaining(timeRemaining)}
          </span>
        </div>
      {/if}
      {#if instance.startNotes}
        <hr class="border-border my-2" />
        <div class="text-sm">
          <span class="text-muted-foreground">Notes:</span>
          <span>{instance.startNotes}</span>
        </div>
      {/if}
      {#if instance.startedAt}
        <div class="text-xs text-muted-foreground mt-2">
          Started {new Date(instance.startedAt).toLocaleString(formatLocale(), {
            month: "short",
            day: "numeric",
            hour: "2-digit",
            minute: "2-digit",
          })}
        </div>
      {/if}
    </div>
    <div class="p-2 border-t border-border">
      <Button
        variant="outline"
        size="sm"
        class="w-full"
        onclick={handleComplete}
      >
        <Check class="h-4 w-4 mr-2" />
        Complete tracker
      </Button>
    </div>
  </Popover.Content>
</Popover.Root>
