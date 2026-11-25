<script lang="ts">
  import { EmptyState } from "#lib/components/shared/index.js";
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "#lib/components/ui/card/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import Target from "@lucide/svelte/icons/target";
  import Pencil from "@lucide/svelte/icons/pencil";
  import ScheduleView from "./ScheduleView.svelte";
  import GlucoseScheduleEditor from "./GlucoseScheduleEditor.svelte";
  import { createTargetRangeSchedule } from "#lib/api/generated/profiles.generated.remote.js";
  import { bgLabel } from "#lib/utils/formatting.js";
  import type { TargetRangeSchedule } from "@nocturne/api-client";

  interface Props {
    profileName: string;
    /** The profile's current target range schedule, or null when none exists yet */
    schedule: TargetRangeSchedule | null;
    /** Disables editing (externally-managed profiles) */
    readOnly?: boolean;
  }

  let { profileName, schedule, readOnly = false }: Props = $props();

  let editing = $state(false);

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
    title="Target range"
    description="Desired blood glucose range"
    unit={bgLabel()}
    icon={Target}
    iconClass="text-glucose-in-range"
    range
    entries={schedule?.entries ?? []}
    {save}
    cancel={() => (editing = false)}
    saveErrorMessage="Failed to save target range"
  />
{:else if schedule?.entries?.length}
  <ScheduleView
    title="Target range"
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
        <div class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-primary/10">
          <Target class="h-5 w-5 text-glucose-in-range" />
        </div>
        <div>
          <CardTitle class="text-base">Target range</CardTitle>
          <CardDescription size="sm">Desired blood glucose range</CardDescription>
        </div>
      </div>
    </CardHeader>
    <CardContent>
      <EmptyState
        art="report-pages"
        size="compact"
        title="No target range configured"
        action={readOnly ? undefined : setTargetRangeAction}
      />
    </CardContent>
  </Card>
  {#snippet setTargetRangeAction()}
    <Button variant="outline" size="sm" onclick={() => (editing = true)}>
      Set target range
    </Button>
  {/snippet}
{/if}
