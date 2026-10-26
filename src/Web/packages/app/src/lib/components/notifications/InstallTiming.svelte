<script lang="ts">
  import { AutoUpdateMode } from "$lib/api/generated/nocturne-api-client";
  import { formatDayTime } from "$lib/utils/formatting";

  interface Props {
    mode: AutoUpdateMode | string | undefined;
    nextCheckAt?: Date | string | null;
    checkIntervalSeconds?: number | null;
  }

  let { mode, nextCheckAt, checkIntervalSeconds }: Props = $props();

  const hours = $derived(
    checkIntervalSeconds ? Math.max(1, Math.round(checkIntervalSeconds / 3600)) : null
  );
</script>

{#if mode === AutoUpdateMode.Automatic && nextCheckAt}
  Watchtower installs it automatically at about {formatDayTime(nextCheckAt)}.
{:else if mode === AutoUpdateMode.Automatic && hours}
  Watchtower installs it automatically in the next {hours} hours.
{:else if mode === AutoUpdateMode.Automatic}
  Watchtower installs it automatically.
{:else if mode === AutoUpdateMode.Detected && hours}
  Watchtower is running. If it installs updates, it installs this one in the next {hours} hours.
{:else if mode === AutoUpdateMode.Detected}
  Watchtower is running. If it installs updates, it installs this one automatically.
{:else if mode === AutoUpdateMode.MonitorOnly}
  Watchtower only reports updates. Install this update manually.
{:else}
  Install this update manually.
{/if}
