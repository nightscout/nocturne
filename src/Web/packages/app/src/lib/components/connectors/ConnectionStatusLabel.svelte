<script lang="ts">
  import { ConnectionStatus } from "@nocturne/api-client";
  import Ban from "@lucide/svelte/icons/ban";
  import CircleAlert from "@lucide/svelte/icons/circle-alert";
  import CircleCheck from "@lucide/svelte/icons/circle-check";
  import CircleDashed from "@lucide/svelte/icons/circle-dashed";
  import CircleOff from "@lucide/svelte/icons/circle-off";
  import FileCheck from "@lucide/svelte/icons/file-check";
  import Clock from "@lucide/svelte/icons/clock";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import TriangleAlert from "@lucide/svelte/icons/triangle-alert";

  let {
    status,
    syncing = false,
  }: { status: ConnectionStatus | undefined; syncing?: boolean } = $props();
</script>

<span class="inline-flex shrink-0 items-center gap-1.5 text-sm font-medium">
  {#if syncing}
    <Loader2 class="size-4 animate-spin text-info" aria-hidden="true" />
    <span class="text-info">Syncing</span>
  {:else if status === ConnectionStatus.Active}
    <CircleCheck class="size-4 text-success" aria-hidden="true" />
    <span class="text-success">Active</span>
  {:else if status === ConnectionStatus.Error}
    <CircleAlert class="size-4 text-destructive" aria-hidden="true" />
    <span class="text-destructive">Error</span>
  {:else if status === ConnectionStatus.Stopped}
    <TriangleAlert class="size-4 text-warning" aria-hidden="true" />
    <span class="text-warning">Stopped</span>
  {:else if status === ConnectionStatus.Waiting}
    <Clock class="size-4 text-muted-foreground" aria-hidden="true" />
    <span class="text-muted-foreground">Waiting for first use</span>
  {:else if status === ConnectionStatus.Quiet}
    <CircleDashed class="size-4 text-muted-foreground" aria-hidden="true" />
    <span class="text-muted-foreground">Quiet</span>
  {:else if status === ConnectionStatus.Imported}
    <FileCheck class="size-4 text-muted-foreground" aria-hidden="true" />
    <span class="text-muted-foreground">Imported</span>
  {:else if status === ConnectionStatus.Disabled}
    <CircleOff class="size-4 text-muted-foreground" aria-hidden="true" />
    <span class="text-muted-foreground">Off</span>
  {:else if status === ConnectionStatus.Revoked}
    <Ban class="size-4 text-muted-foreground" aria-hidden="true" />
    <span class="text-muted-foreground">Revoked</span>
  {/if}
</span>
