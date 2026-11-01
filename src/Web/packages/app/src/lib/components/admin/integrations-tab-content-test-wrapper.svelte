<script lang="ts">
  import * as Tabs from "#lib/components/ui/tabs/index.js";
  import IntegrationsTabContent from "./IntegrationsTabContent.svelte";
  import type { ComponentProps } from "svelte";
  import type { PlatformSettingsSummary } from "#lib/api/index.js";

  let {
    refreshed,
    ...componentProps
  }: ComponentProps<typeof IntegrationsTabContent> & {
    /** The summaries the page reloads after a save, as the admin page does. */
    refreshed?: PlatformSettingsSummary[];
  } = $props();

  // svelte-ignore state_referenced_locally
  let platforms = $state(componentProps.platforms);
</script>

<Tabs.Root value="integrations">
  <IntegrationsTabContent
    {...componentProps}
    {platforms}
    onSave={async (category, enabled, fields) => {
      await componentProps.onSave(category, enabled, fields);
      if (refreshed) platforms = refreshed;
    }}
  />
</Tabs.Root>
