<script lang="ts">
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { Target, Pencil } from "lucide-svelte";
  import ScheduleView from "./ScheduleView.svelte";
  import { createTargetRangeSchedule } from "$api/generated/profiles.generated.remote";
  import { describeSubmitError } from "$lib/forms/submit-error";
  import { glucoseUnits } from "$lib/stores/appearance-store.svelte";
  import {
    bgLabel,
    bgThresholds,
    convertToDisplayUnits,
    convertFromDisplayUnits,
    timeStringToSeconds,
  } from "$lib/utils/formatting";
  import type { TargetRangeSchedule } from "$api-clients";

  interface Props {
    profileName: string;
    /** The profile's current target range schedule, or null when none exists yet */
    schedule: TargetRangeSchedule | null;
    /** Disables editing (externally-managed profiles) */
    readOnly?: boolean;
  }

  interface DraftEntry {
    time?: string;
    low?: number;
    high?: number;
  }

  let { profileName, schedule, readOnly = false }: Props = $props();

  let editing = $state(false);
  let saving = $state(false);
  let saveError = $state<string | null>(null);
  let draft = $state<DraftEntry[]>([]);

  const displayUnits = $derived(glucoseUnits.current);

  function startEdit() {
    if (schedule?.entries?.length) {
      // Schedule values are mg/dL (the TargetRangeEntry contract); convert to the user's
      // display units for editing.
      draft = schedule.entries.map((e) => ({
        time: e.time ?? "00:00",
        low: convertToDisplayUnits(e.low ?? 0, displayUnits),
        high: convertToDisplayUnits(e.high ?? 0, displayUnits),
      }));
    } else {
      const { targetLow, targetHigh } = bgThresholds();
      draft = [{ time: "00:00", low: targetLow, high: targetHigh }];
    }
    saveError = null;
    editing = true;
  }

  const validationError = $derived.by(() => {
    if (draft.some((e) => !e.time)) return "Every time block needs a start time.";
    if (draft.some((e) => (e.low ?? 0) <= 0 || (e.high ?? 0) <= 0))
      return "Low and high targets must be greater than zero.";
    if (draft.some((e) => (e.low ?? 0) >= (e.high ?? Infinity)))
      return "Each low target must be below its high target.";
    if (new Set(draft.map((e) => e.time)).size !== draft.length)
      return "Time blocks must have unique start times.";
    return null;
  });

  async function save() {
    if (validationError) return;
    saving = true;
    saveError = null;
    try {
      // Each save creates a new timestamped record rather than updating in place, so prior
      // ranges are preserved (e.g. a tightened pregnancy range can be reverted later). Reports
      // and alerts resolve the newest schedule for the active profile, so a subsequent uploader
      // profile sync (a newer record) supersedes a hand edit.
      await createTargetRangeSchedule({
        profileName,
        timestamp: new Date().toISOString(),
        dataSource: "manual",
        entries: draft.map((e) => ({
          time: e.time,
          timeAsSeconds: timeStringToSeconds(e.time),
          low: convertFromDisplayUnits(e.low ?? 0, displayUnits),
          high: convertFromDisplayUnits(e.high ?? 0, displayUnits),
        })),
      });
      editing = false;
    } catch (err) {
      saveError = describeSubmitError(err, "Failed to save target range");
    } finally {
      saving = false;
    }
  }
</script>

{#if editing}
  <Card>
    <CardContent class="space-y-4 pt-6">
      <ScheduleView
        title="Target Range"
        description="Desired blood glucose range"
        unit={bgLabel()}
        icon={Target}
        iconClass="text-amber-600"
        entries={draft}
        onchange={(entries) => (draft = entries)}
        step={displayUnits === "mmol" ? 0.1 : 1}
        min={displayUnits === "mmol" ? 0.1 : 1}
      />
      {#if validationError}
        <p class="text-sm text-destructive">{validationError}</p>
      {/if}
      {#if saveError}
        <p class="text-sm text-destructive">{saveError}</p>
      {/if}
      <div class="flex justify-end gap-2">
        <Button variant="outline" disabled={saving} onclick={() => (editing = false)}>
          Cancel
        </Button>
        <Button disabled={saving || !!validationError} onclick={save}>
          {saving ? "Saving..." : "Save"}
        </Button>
      </div>
    </CardContent>
  </Card>
{:else if schedule?.entries?.length}
  <ScheduleView
    title="Target Range"
    description="Desired blood glucose range"
    unit={bgLabel()}
    icon={Target}
    iconClass="text-amber-600"
    entries={schedule.entries}
    sourceUnits="mg/dl"
  >
    {#snippet actions()}
      {#if !readOnly}
        <Button variant="ghost" size="sm" onclick={startEdit}>
          <Pencil class="mr-1 h-4 w-4" />
          Edit
        </Button>
      {/if}
    {/snippet}
  </ScheduleView>
{:else}
  <Card>
    <CardHeader class="pb-3">
      <div class="flex items-center gap-3">
        <div class="flex h-10 w-10 items-center justify-center rounded-lg bg-primary/10">
          <Target class="h-5 w-5 text-amber-600" />
        </div>
        <div>
          <CardTitle class="text-base">Target Range</CardTitle>
          <CardDescription class="text-xs">Desired blood glucose range</CardDescription>
        </div>
      </div>
    </CardHeader>
    <CardContent>
      <p class="text-sm text-muted-foreground">No target range configured.</p>
      {#if !readOnly}
        <Button variant="outline" size="sm" class="mt-3" onclick={startEdit}>
          Set Target Range
        </Button>
      {/if}
    </CardContent>
  </Card>
{/if}
