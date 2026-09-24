<script lang="ts">
  import { formatLocale } from "$lib/utils/formatting";
  import * as Popover from "$lib/components/ui/popover";
  import { Button } from "$lib/components/ui/button";
  import type { TrackerInstanceDto, TrackerDefinitionDto } from "$lib/api";
  import { NotificationUrgency, TrackerCategory } from "$lib/api";
  import { cn } from "$lib/utils";
  import { Check, Clock, TriangleAlert } from "lucide-svelte";
  import { TrackerCategoryIcon } from "$lib/components/icons";

  type AlertLevel = "none" | "info" | "warn" | "hazard" | "urgent";

  interface TrackerPillProps {
    /** The tracker instance to display */
    instance: TrackerInstanceDto;
    /** The tracker definition for metadata */
    definition?: TrackerDefinitionDto;
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
    class: className,
    onComplete,
  }: TrackerPillProps = $props();

  let popoverOpen = $state(false);

  // Format age display
  function formatAge(hours: number | undefined): string {
    if (hours === undefined || hours === null) return "n/a";
    if (hours < 1) return `${Math.floor(hours * 60)}m`;
    if (hours < 24) return `${Math.floor(hours)}h`;
    const days = Math.floor(hours / 24);
    const h = Math.floor(hours % 24);
    return h > 0 ? `${days}d ${h}h` : `${days}d`;
  }

  // Format lifespan display
  function formatLifespan(hours: number | undefined): string {
    if (hours === undefined || hours === null) return "Not set";
    if (hours < 24) return `${hours} hours`;
    const days = Math.floor(hours / 24);
    return days === 1 ? "1 day" : `${days} days`;
  }

  // Calculate time remaining
  const timeRemaining = $derived.by(() => {
    if (!instance.ageHours || !definition?.lifespanHours) return undefined;
    return definition.lifespanHours - instance.ageHours;
  });

  // Calculate progress percentage (capped at 100%)
  const progressPercent = $derived.by(() => {
    if (!instance.ageHours || !definition?.lifespanHours) return 0;
    return Math.min(100, (instance.ageHours / definition.lifespanHours) * 100);
  });

  // Only show progress bar if over 10%
  const showProgress = $derived(progressPercent > 10);

  // Format time remaining
  function formatTimeRemaining(hours: number | undefined): string {
    if (hours === undefined) return "Unknown";
    if (hours <= 0) return "Overdue";
    return formatAge(hours);
  }

  // Determine alert level based on thresholds
  const level = $derived.by((): AlertLevel => {
    if (!instance.ageHours || !definition?.notificationThresholds)
      return "none";

    const age = instance.ageHours;
    const thresholds = definition.notificationThresholds.sort(
      (a, b) => (b.hours ?? 0) - (a.hours ?? 0)
    );

    for (const threshold of thresholds) {
      if (threshold.hours && age >= threshold.hours) {
        const urgency = threshold.urgency;
        if (urgency === NotificationUrgency.Urgent) return "urgent";
        if (urgency === NotificationUrgency.Hazard) return "hazard";
        if (urgency === NotificationUrgency.Warn) return "warn";
        if (urgency === NotificationUrgency.Info) return "info";
      }
    }
    return "none";
  });

  // eslint-disable-next-line shadcn/require-static-classes -- this is the pill component: levelClasses is its variant table, and PopoverTrigger renders unstyled.
  const pillClasses = $derived(
    cn(
      "relative inline-flex flex-col items-start rounded-md px-3 py-1 text-left whitespace-nowrap transition-colors cursor-pointer select-none overflow-hidden hover:bg-accent/50",
      className
    )
  );

  const valueTone: Record<AlertLevel, string> = {
    none: "",
    info: "text-severity-info",
    warn: "text-severity-warn",
    hazard: "text-severity-hazard",
    urgent: "text-severity-urgent",
  };

  // The share of the tracker's lifespan used, drawn as a hairline under the item.
  const progressFillClasses: Record<AlertLevel, string> = {
    none: "bg-muted-foreground/40",
    info: "bg-severity-info",
    warn: "bg-severity-warn",
    hazard: "bg-severity-hazard",
    urgent: "bg-severity-urgent",
  };

  const label = $derived(
    instance.definitionName ?? definition?.name ?? "Tracker"
  );
  const ageDisplay = $derived(formatAge(instance.ageHours));

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
  <Popover.Trigger class={pillClasses}>
    {#if showProgress}
      <span
        class="absolute bottom-0 left-3 h-px {progressFillClasses[level]} w-[calc((100%-1.5rem)*var(--progress))] transition-all duration-500 ease-out"
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
    <span class="flex items-center gap-1 text-sm font-medium tabular-nums {valueTone[level]}">
      {#if level === "warn" || level === "hazard" || level === "urgent"}
        <TriangleAlert class="size-3.5" aria-hidden="true" />
        <span class="sr-only">{level === "urgent" ? "Urgent:" : "Warning:"}</span>
      {/if}
      {ageDisplay}
    </span>
  </Popover.Trigger>
  <Popover.Content class="w-72 p-0" align="center" side="bottom">
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
          Running Time
        </span>
        <span class="font-medium">{ageDisplay}</span>
      </div>
      {#if definition?.lifespanHours}
        <div class="flex justify-between items-center text-sm">
          <span class="text-muted-foreground">Expected Lifespan</span>
          <span class="font-medium">
            {formatLifespan(definition.lifespanHours)}
          </span>
        </div>
        <div class="flex justify-between items-center text-sm">
          <span class="text-muted-foreground">Time Remaining</span>
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
        Complete Tracker
      </Button>
    </div>
  </Popover.Content>
</Popover.Root>
