<script lang="ts">
  import type {
    InAppNotificationDto,
    UndecryptableAlertChannel,
  } from "#lib/api/generated/nocturne-api-client.js";
  import { formatList } from "#lib/components/members/data-categories.js";
  import { platformCategoryName } from "#lib/utils/platform-categories.js";

  interface Props {
    metadata: InAppNotificationDto["metadata"];
    platform: boolean;
  }

  let { metadata, platform }: Props = $props();

  function strings(value: unknown): string[] {
    return Array.isArray(value) ? value.filter((v) => typeof v === "string") : [];
  }

  function channelName(channel: UndecryptableAlertChannel): string {
    const rule = channel.ruleName ?? "";
    return channel.destination ? `${rule} (${channel.destination})` : rule;
  }

  const connectors = $derived(strings(metadata?.["Connectors"]));
  const alertChannels = $derived(
    (Array.isArray(metadata?.["AlertChannels"]) ? metadata["AlertChannels"] : []).map(channelName)
  );
  const platforms = $derived(strings(metadata?.["PlatformSettings"]).map(platformCategoryName));
</script>

{#if platform}
  <p>
    The instance key changed. Nocturne cannot read the saved credentials for {formatList(
      platforms
    )}. Enter them again in the platform settings.
  </p>
{:else}
  <p>
    The instance key changed. Nocturne cannot read the saved secrets for these items. Enter each
    secret again.
  </p>
  {#if connectors.length > 0}
    <p data-testid="instance-key-connectors">Connectors: {formatList(connectors)}</p>
  {/if}
  {#if alertChannels.length > 0}
    <p data-testid="instance-key-alert-channels">Alert webhooks: {formatList(alertChannels)}</p>
  {/if}
{/if}
