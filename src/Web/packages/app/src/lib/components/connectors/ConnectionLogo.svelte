<script lang="ts">
  import {
    ConnectionKind,
    type Connection,
  } from "#lib/api/generated/nocturne-api-client.js";
  import AppLogo from "#lib/components/ui/AppLogo.svelte";
  import AppWindow from "@lucide/svelte/icons/app-window";
  import KeyRound from "@lucide/svelte/icons/key-round";

  let {
    connection,
  }: { connection: Pick<Connection, "kind" | "icon" | "logoUri"> } = $props();

  let failedLogoUri = $state<string | null>(null);
  const logoUri = $derived(
    connection.logoUri && connection.logoUri !== failedLogoUri
      ? connection.logoUri
      : null
  );
</script>

<span
  class="flex size-8 shrink-0 items-center justify-center text-muted-foreground"
  aria-hidden="true"
>
  {#if logoUri}
    <img
      src={logoUri}
      alt=""
      class="size-8 rounded-md object-contain"
      draggable="false"
      onerror={() => (failedLogoUri = logoUri)}
    />
  {:else if connection.icon}
    <AppLogo icon={connection.icon} invertMode class="size-8 object-contain" />
  {:else if connection.kind === ConnectionKind.Token}
    <KeyRound class="size-5" />
  {:else}
    <AppWindow class="size-5" />
  {/if}
</span>
