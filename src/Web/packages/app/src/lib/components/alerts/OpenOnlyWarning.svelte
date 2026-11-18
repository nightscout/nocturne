<script lang="ts">
  import { getChannelStatuses } from "#lib/api/generated/systems.generated.remote.js";
  import * as Alert from "#lib/components/ui/alert/index.js";
  import TriangleAlert from "@lucide/svelte/icons/triangle-alert";
  import type { ChannelDef } from "./types";

  interface Props {
    channels: ChannelDef[];
  }

  let { channels }: Props = $props();

  // The API says which kinds reach someone while no Nocturne page is open.
  const statusQuery = getChannelStatuses();
  const reachesWhileClosed = $derived(
    new Set(
      (statusQuery.current?.channels ?? [])
        .filter((c) => c.deliversWhileClosed)
        .map((c) => c.channelType),
    ),
  );
  const onlyWhileOpen = $derived(
    statusQuery.current != null &&
      channels.length > 0 &&
      !channels.some((c) => reachesWhileClosed.has(c.channelType)),
  );
</script>

{#if onlyWhileOpen}
  <Alert.Root variant="warning" data-testid="alert-open-only">
    <TriangleAlert />
    <Alert.Description>
      Nocturne cannot wake you with this. These alerts only show while Nocturne is open and the
      device is awake. To get alerts at night, or when Nocturne is closed, add a place such as
      Telegram, WhatsApp or email.
    </Alert.Description>
  </Alert.Root>
{/if}
