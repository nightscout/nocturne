<script lang="ts">
  import { Play, CheckCircle, CalendarClock, Check } from "lucide-svelte";
  import { Button } from "$lib/components/ui/button";
  import { TrackerCategoryIcon } from "$lib/components/icons";
  import { cn } from "$lib/utils";
  import type {
    TrackerInstanceDto,
    TrackerDefinitionDto,
    TrackerCategory,
  } from "$api";

  let {
    event,
    category,
    startTime,
    level,
    formatTrackerAge,
    openCompletionDialog,
    def,
  } = $props<{
    event: {
      instance: TrackerInstanceDto;
      eventType: "start" | "due" | "completed";
      date: string;
    };
    category: TrackerCategory;
    startTime: string | null;
    level: string;
    formatTrackerAge: (hours: number | undefined) => string;
    openCompletionDialog: (instance: TrackerInstanceDto, def: TrackerDefinitionDto | undefined, date: string) => void;
    def: TrackerDefinitionDto | undefined;
  }>();
</script>

<div class="space-y-2">
  <div class="flex items-center gap-2">
    <TrackerCategoryIcon
      {category}
      class="h-4 w-4 text-muted-foreground"
    />
    <span class="font-medium text-sm">
      {event.instance.definitionName}
    </span>
  </div>
  <div class="text-xs space-y-1">
    {#if event.eventType === "start"}
      <div
        class="flex items-center gap-1 text-green-600 dark:text-green-400"
      >
        <Play class="h-3 w-3" />
        <span>
          {startTime
            ? `Started at ${startTime}`
            : "Started on this day"}
        </span>
      </div>
      {#if event.instance.startNotes}
        <div class="text-muted-foreground">
          Note: {event.instance.startNotes}
        </div>
      {/if}
    {:else if event.eventType === "completed"}
      <div
        class="flex items-center gap-1 text-muted-foreground"
      >
        <CheckCircle class="h-3 w-3" />
        <span>Completed on this day</span>
      </div>
      {#if event.instance.completionNotes}
        <div class="text-muted-foreground">
          Note: {event.instance.completionNotes}
        </div>
      {/if}
    {:else}
      <div
        class={cn(
          "flex items-center gap-1",
          level === "urgent"
            ? "text-red-600 dark:text-red-400"
            : level === "hazard"
              ? "text-orange-600 dark:text-orange-400"
              : level === "warn"
                ? "text-yellow-600 dark:text-yellow-400"
                : "text-blue-600 dark:text-blue-400"
        )}
      >
        <CalendarClock class="h-3 w-3" />
        <span>Due on this day</span>
      </div>
      <div class="text-muted-foreground">
        Age: {formatTrackerAge(event.instance.ageHours)}
      </div>
      <Button
        size="sm"
        variant="outline"
        class="mt-2 w-full h-7 text-xs"
        onclick={() =>
          openCompletionDialog(
            event.instance,
            def,
            event.date
          )}
      >
        <Check class="h-3 w-3 mr-1" />
        Complete
      </Button>
    {/if}
  </div>
</div>
