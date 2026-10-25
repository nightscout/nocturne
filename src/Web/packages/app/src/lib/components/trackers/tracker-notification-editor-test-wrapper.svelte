<script lang="ts">
  import { TrackerMode } from "$lib/api";
  import * as Tooltip from "$lib/components/ui/tooltip";
  import TrackerNotificationEditor from "./tracker-notification-editor.svelte";
  import type { TrackerNotification } from "./types";

  interface Props {
    notifications?: TrackerNotification[];
    class?: string;
    mode?: TrackerMode;
    lifespanHours?: number | undefined;
  }

  let { notifications = $bindable([]), mode, lifespanHours, ...rest }: Props = $props();
</script>

<Tooltip.Provider>
  <TrackerNotificationEditor bind:notifications {mode} {lifespanHours} {...rest} />
</Tooltip.Provider>
