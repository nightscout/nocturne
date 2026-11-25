<script lang="ts">
  import { DashboardVisibility } from "#lib/api/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import * as Select from "#lib/components/ui/select/index.js";
  import type { TrackerDraft } from "./draft";

  interface Props {
    /** Edited in place. */
    draft: TrackerDraft;
  }

  let { draft = $bindable() }: Props = $props();

  const OPTIONS = [
    DashboardVisibility.Off,
    DashboardVisibility.Always,
    DashboardVisibility.Info,
    DashboardVisibility.Warn,
    DashboardVisibility.Hazard,
    DashboardVisibility.Urgent,
  ];

  function label(visibility: DashboardVisibility): string {
    switch (visibility) {
      case DashboardVisibility.Off:
        return "Off - Do not show on dashboard";
      case DashboardVisibility.Info:
        return "Show after Info threshold";
      case DashboardVisibility.Warn:
        return "Show after Warn threshold";
      case DashboardVisibility.Hazard:
        return "Show after Hazard threshold";
      case DashboardVisibility.Urgent:
        return "Show after Urgent threshold";
      default:
        return "Always show";
    }
  }
</script>

<div class="space-y-2">
  <Label for="dashboardVisibility">Dashboard visibility</Label>
  <Select.Root type="single" bind:value={draft.dashboardVisibility}>
    <Select.Trigger id="dashboardVisibility">{label(draft.dashboardVisibility)}</Select.Trigger>
    <Select.Content>
      {#each OPTIONS as option (option)}
        <Select.Item value={option} label={label(option)} />
      {/each}
    </Select.Content>
  </Select.Root>
  <p class="text-xs text-muted-foreground">
    When to show this tracker as a pill on the dashboard
  </p>
</div>
