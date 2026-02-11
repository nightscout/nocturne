<script lang="ts">
  import * as Dialog from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import { Label } from "$lib/components/ui/label";
  import { Input } from "$lib/components/ui/input";
  import { TextareaAutosize } from "$lib/components/ui/textarea";
  import { Play } from "lucide-svelte";
  import { cn } from "$lib/utils";

  import {
    type TrackerDefinitionDto,
    type TrackerInstanceDto,
    TrackerCategory,
    TrackerMode,
    CompletionReason,
  } from "$api";
  import * as trackersRemote from "$lib/data/generated/trackers.generated.remote";
  import * as treatmentsRemote from "$lib/data/treatments.remote";

  interface TrackerStartDialogProps {
    open: boolean;
    definition: TrackerDefinitionDto | null;
    history?: TrackerInstanceDto[];
    onClose: () => void;
    onStart?: () => void;
  }

  let {
    open = $bindable(false),
    definition,
    history = [],
    onClose,
    onStart,
  }: TrackerStartDialogProps = $props();

  let startNotes = $state("");
  let startedAtString = $state("");
  let scheduledAtString = $state("");
  let isSubmitting = $state(false);

  // Derived mode check
  const isEventMode = $derived(definition?.mode === TrackerMode.Event);

  // Completion reason labels for history display
  const completionReasonLabels: Record<CompletionReason, string> = {
    [CompletionReason.Completed]: "Completed",
    [CompletionReason.Expired]: "Expired",
    [CompletionReason.Other]: "Other",
    [CompletionReason.Failed]: "Failed",
    [CompletionReason.FellOff]: "Fell Off",
    [CompletionReason.ReplacedEarly]: "Replaced Early",
    [CompletionReason.Empty]: "Empty",
    [CompletionReason.Refilled]: "Refilled",
    [CompletionReason.Attended]: "Attended",
    [CompletionReason.Rescheduled]: "Rescheduled",
    [CompletionReason.Cancelled]: "Cancelled",
    [CompletionReason.Missed]: "Missed",
  };

  // Reset form when dialog opens
  $effect(() => {
    if (open) {
      startNotes = "";
      const now = new Date();
      const offset = now.getTimezoneOffset() * 60000;
      startedAtString = new Date(now.getTime() - offset)
        .toISOString()
        .slice(0, 16);
      // Default scheduled time to tomorrow at 9am for event mode
      const tomorrow = new Date(now.getTime() + 24 * 60 * 60 * 1000);
      tomorrow.setHours(9, 0, 0, 0);
      scheduledAtString = new Date(tomorrow.getTime() - offset)
        .toISOString()
        .slice(0, 16);
    }
  });

  const lastCompletion = $derived.by(() => {
    if (!definition?.id || history.length === 0) return null;
    const defId = definition.id;
    const historyForDef = history
      .filter((i) => i.definitionId === defId)
      .sort((a, b) => {
        const dateA = a.completedAt ? new Date(a.completedAt).getTime() : 0;
        const dateB = b.completedAt ? new Date(b.completedAt).getTime() : 0;
        return dateB - dateA;
      });
    return historyForDef.length > 0 ? historyForDef[0] : null;
  });

  function formatLastCompletion(date: Date | string): string {
    const d = new Date(date);
    const now = new Date();

    const isSameDay = (a: Date, b: Date) =>
      a.getFullYear() === b.getFullYear() &&
      a.getMonth() === b.getMonth() &&
      a.getDate() === b.getDate();

    const yesterday = new Date(now);
    yesterday.setDate(yesterday.getDate() - 1);

    const timeStr = d.toLocaleTimeString("en-US", {
      hour: "numeric",
      minute: "2-digit",
      hour12: true,
    });

    if (isSameDay(d, now)) {
      return `today at ${timeStr}`;
    } else if (isSameDay(d, yesterday)) {
      return `yesterday at ${timeStr}`;
    } else {
      const dateStr = d.toLocaleDateString("en-US", {
        month: "short",
        day: "numeric",
      });
      return `on ${dateStr} at ${timeStr}`;
    }
  }

  function isDefaultReason(
    reason: CompletionReason | undefined,
    category: TrackerCategory
  ): boolean {
    if (!reason) return true;
    switch (category) {
      case TrackerCategory.Reservoir:
        return reason === CompletionReason.Refilled;
      case TrackerCategory.Appointment:
        return reason === CompletionReason.Attended;
      default:
        return reason === CompletionReason.Completed;
    }
  }

  const startPreview = $derived.by(() => {
    if (!definition || !definition.notificationThresholds)
      return [];

    const now = new Date();
    let referenceTime: Date;

    if (isEventMode) {
      if (!scheduledAtString) return [];
      referenceTime = new Date(scheduledAtString);
    } else {
      if (!startedAtString) return [];
      referenceTime = new Date(startedAtString);
    }

    if (isNaN(referenceTime.getTime())) return [];

    return definition.notificationThresholds
      .filter((n) => n.hours !== undefined)
      .map((n) => {
        // For event mode, hours are relative to scheduled time (negative = before)
        // For duration mode, hours are relative to start time
        const triggerTime = new Date(
          referenceTime.getTime() + n.hours! * 60 * 60 * 1000
        );
        const timeUntil = triggerTime.getTime() - now.getTime();
        const hoursUntil = timeUntil / (1000 * 60 * 60);

        return {
          ...n,
          triggerTime,
          isPast: timeUntil < 0,
          relativeTime:
            Math.abs(hoursUntil) < 1
              ? `${Math.abs(Math.round(hoursUntil * 60))} mins`
              : `${Math.abs(hoursUntil).toFixed(1)} hours`,
        };
      })
      .sort((a, b) => (a.hours ?? 0) - (b.hours ?? 0));
  });

  async function handleStart() {
    if (!definition?.id) return;
    isSubmitting = true;
    const defId = definition.id;
    const startedAt = startedAtString ? new Date(startedAtString) : undefined;
    const scheduledAt = scheduledAtString ? new Date(scheduledAtString) : undefined;

    try {
      await trackersRemote.startInstance({
        definitionId: defId,
        startNotes: startNotes || undefined,
        startedAt: isEventMode ? undefined : (startedAt ? startedAt.toISOString() : undefined),
        scheduledAt: isEventMode ? (scheduledAt ? scheduledAt.toISOString() : undefined) : undefined,
      });

      // Create treatment event if configured on the definition
      if (definition.startEventType) {
        await treatmentsRemote.createTreatment({
          eventType: definition.startEventType,
          created_at: (startedAt ?? new Date()).toISOString(),
          notes: startNotes || undefined,
          enteredBy: "Nocturne Tracker",
        });
      }

      open = false;
      onStart?.();
    } catch (err) {
      console.error("Failed to start instance:", err);
    } finally {
      isSubmitting = false;
    }
  }

  function handleClose() {
    open = false;
    onClose();
  }
</script>

<Dialog.Root bind:open>
  <Dialog.Content class="sm:max-w-[425px]">
    <Dialog.Header>
      <Dialog.Title>Start {definition?.name ?? "Tracker"}</Dialog.Title>
      <Dialog.Description>
        {#if lastCompletion}
          {@const reason = lastCompletion.completionReason}
          {@const category = definition?.category ?? TrackerCategory.Consumable}
          <div class="flex flex-col gap-1 mt-1">
            <span class="text-xs text-muted-foreground">
              Last completed {formatLastCompletion(
                (lastCompletion.completedAt ?? lastCompletion.startedAt)!
              )}
              {#if !isDefaultReason(reason, category)}
                ({completionReasonLabels[reason ?? CompletionReason.Completed]})
              {/if}
            </span>
          </div>
        {:else}
          Begin tracking {definition?.name ?? "tracker"}.
        {/if}
      </Dialog.Description>
    </Dialog.Header>
    <div class="grid gap-4 py-4">
      {#if isEventMode}
        <div class="space-y-2">
          <Label for="scheduledAt">Scheduled For</Label>
          <Input
            type="datetime-local"
            id="scheduledAt"
            bind:value={scheduledAtString}
          />
          <p class="text-[10px] text-muted-foreground">
            When is this event scheduled?
          </p>
        </div>
      {:else}
        <div class="space-y-2">
          <Label for="startedAt">Start Time</Label>
          <Input
            type="datetime-local"
            id="startedAt"
            bind:value={startedAtString}
          />
          <p class="text-[10px] text-muted-foreground">
            Adjust if you started this earlier.
          </p>
        </div>
      {/if}

      <div class="space-y-2">
        <Label for="startNotes">Notes (optional)</Label>
        <TextareaAutosize
          bind:value={startNotes}
          placeholder={isEventMode ? "e.g., Dr. Smith, Room 204" : "e.g., Left arm, Lot #12345"}
        />
      </div>

      {#if startPreview.length > 0}
        <div class="rounded-lg border bg-muted/50 p-3 mt-2">
          <Label class="text-xs mb-2 block font-medium">
            Notification Schedule (Adjusted)
          </Label>
          <div class="space-y-2">
            {#each startPreview as preview}
              {@const isPast = preview.isPast}
              {@const urgencyLower = String(preview.urgency).toLowerCase()}
              <div class="flex items-center justify-between text-xs">
                <div class="flex items-center gap-2">
                  <div
                    class={cn(
                      "w-2 h-2 rounded-full",
                      (urgencyLower === "info" || urgencyLower === "0") &&
                        "bg-blue-500",
                      (urgencyLower === "warn" || urgencyLower === "1") &&
                        "bg-yellow-500",
                      (urgencyLower === "hazard" || urgencyLower === "2") &&
                        "bg-orange-500",
                      (urgencyLower === "urgent" || urgencyLower === "3") &&
                        "bg-red-500"
                    )}
                  ></div>
                  <span>{preview.hours}h</span>
                </div>
                <div
                  class={cn(
                    "flex flex-col items-end",
                    isPast ? "text-destructive" : "text-muted-foreground"
                  )}
                >
                  <span>
                    {isPast ? "Triggered" : "Triggering in"}
                    {preview.relativeTime}
                  </span>
                  <span class="text-[10px] opacity-70">
                    {preview.triggerTime.toLocaleTimeString()}
                  </span>
                </div>
              </div>
            {/each}
          </div>
        </div>
      {/if}
    </div>
    <Dialog.Footer>
      <Button variant="outline" onclick={handleClose} disabled={isSubmitting}>
        Cancel
      </Button>
      <Button onclick={handleStart} disabled={isSubmitting}>
        <Play class="h-4 w-4 mr-2" />
        {isEventMode ? "Schedule" : "Start"}
      </Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>
