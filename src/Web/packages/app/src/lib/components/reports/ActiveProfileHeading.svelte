<script lang="ts">
  import type { ActiveProfileSchedules } from "$lib/api/generated/nocturne-api-client";

  interface Props {
    profile: ActiveProfileSchedules;
    /** Whether the window read more than one profile, so each needs its name and share. */
    multiple: boolean;
  }

  let { profile, multiple }: Props = $props();

  const percentage = $derived(
    profile.percentage != null && profile.percentage !== 100 ? profile.percentage : null
  );
  const timeshift = $derived(profile.timeshiftHours || null);
  const sharePercent = $derived(Math.round((profile.share ?? 0) * 100));
</script>

{#if multiple || percentage != null || timeshift != null}
  <p class="text-xs font-medium text-muted-foreground">
    <span class="text-foreground">{profile.profileName}</span>
    {#if multiple}
      · {sharePercent === 0 && (profile.share ?? 0) > 0 ? "<1" : sharePercent}%
    {/if}
    {#if percentage != null}
      · at {percentage}%
    {/if}
    {#if timeshift != null}
      · shifted {timeshift > 0 ? "+" : ""}{timeshift}h
    {/if}
    {#if percentage != null || timeshift != null}
      {#if profile.adjustmentApplied}
        (already in the schedules shown)
      {:else}
        (not applied to the schedules shown)
      {/if}
    {/if}
  </p>
{/if}
