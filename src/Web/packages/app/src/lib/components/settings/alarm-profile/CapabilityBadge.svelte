<script lang="ts">
  import type { BrowserAlarmCapabilities } from "$lib/audio/alarm-sounds";

  interface Props {
    capabilities: BrowserAlarmCapabilities | null;
    feature: "notifications" | "vibration" | "wakeLock";
  }

  let { capabilities, feature }: Props = $props();
</script>

{#if capabilities && !capabilities[feature]}
  <span
    class="px-1.5 py-0.5 text-2xs font-medium rounded bg-muted text-muted-foreground"
  >
    Not on this device
  </span>
{:else if feature === "notifications" && capabilities && capabilities.notificationPermission === "denied"}
  <span
    class="px-1.5 py-0.5 text-2xs font-medium rounded bg-destructive/10 text-destructive"
  >
    Blocked on this device
  </span>
{:else if feature === "notifications" && capabilities && capabilities.notificationPermission === "default"}
  <span
    class="px-1.5 py-0.5 text-2xs font-medium rounded bg-warning/10 text-warning"
  >
    Needs Permission
  </span>
{/if}
