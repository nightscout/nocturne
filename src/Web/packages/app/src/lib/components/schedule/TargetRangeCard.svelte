<script lang="ts">
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import Target from "@lucide/svelte/icons/target";
  import Pencil from "@lucide/svelte/icons/pencil";
  import ScheduleView from "./ScheduleView.svelte";
  import GlucoseScheduleEditor from "./GlucoseScheduleEditor.svelte";
  import { createTargetRangeSchedule } from "$api/generated/profiles.generated.remote";
  import { bgLabel, bgThresholds } from "$lib/utils/formatting";
  import type { TargetRangeSchedule } from "$api-clients";

  interface Props {
    profileName: string;
    /** The profile's current target range schedule, or null when none exists yet */
    schedule: TargetRangeSchedule | null;
    /** Disables editing (externally-managed profiles) */
    readOnly?: boolean;
  }

  let { profileName, schedule, readOnly = false }: Props = $props();

  let editing = $state(false);

  function defaultTargets() {
    const { targetLow, targetHigh } = bgThresholds();
    return [{ time: "00:00", low: targetLow, high: targetHigh }];
  }

  // Each save creates a new timestamped record rather than updating in place, so prior
  // ranges are preserved (e.g. a tightened pregnancy range can be reverted later). Reports
  // and alerts resolve the newest schedule for the active profile, so a subsequent uploader
  // profile sync (a newer record) supersedes a hand edit.
  async function save(entries: NonNullable<TargetRangeSchedule["entries"]>) {
    await createTargetRangeSchedule({
      profileName,
      timestamp: new Date().toISOString(),
      dataSource: "manual",
      entries,
    });
    editing = false;
  }
</script>

{#if editing}
  <GlucoseScheduleEditor
    title="Target Range"
    description="Desired blood glucose range"
    unit={bgLabel()}
    icon={Target}
    iconClass="text-glucose-in-range"
    range
    entries={schedule?.entries ?? []}
    fallback={defaultTargets}
    {save}
    cancel={() => (editing = false)}
    saveErrorMessage="Failed to save target range"
  />
{:else if schedule?.entries?.length}
  <ScheduleView
    title="Target Range"
    description="Desired blood glucose range"
    unit={bgLabel()}
    icon={Target}
    iconClass="text-glucose-in-range"
    entries={schedule.entries}
    sourceUnits="mg/dl"
  >
    {#snippet actions()}
      {#if !readOnly}
        <Button variant="ghost" size="sm" onclick={() => (editing = true)}>
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
          <Target class="h-5 w-5 text-glucose-in-range" />
        </div>
        <div>
          <CardTitle class="text-base">Target Range</CardTitle>
          <CardDescription size="sm">Desired blood glucose range</CardDescription>
        </div>
      </div>
    </CardHeader>
    <CardContent>
      <p class="text-sm text-muted-foreground">No target range configured.</p>
      {#if !readOnly}
        <Button variant="outline" size="sm" class="mt-3" onclick={() => (editing = true)}>
          Set Target Range
        </Button>
      {/if}
    </CardContent>
  </Card>
{/if}
