<script lang="ts">
  import * as Tabs from "$lib/components/ui/tabs";
  import OidcProvidersTabContent from "./OidcProvidersTabContent.svelte";
  import type { OidcProviderResponse } from "$lib/api/generated/nocturne-api-client";

  interface Props {
    providers: OidcProviderResponse[];
    configManaged: boolean;
    loading: boolean;
    error: string | null;
    onAdd: () => void;
    onEdit: (provider: OidcProviderResponse) => void;
    onDelete: (provider: OidcProviderResponse) => void;
    onToggle: (provider: OidcProviderResponse) => void;
  }

  let { providers, configManaged, loading, error, onAdd, onEdit, onDelete, onToggle }: Props = $props();
</script>

<Tabs.Root value="identity-providers">
  <OidcProvidersTabContent
    {providers}
    {configManaged}
    {loading}
    {error}
    {onAdd}
    {onEdit}
    {onDelete}
    {onToggle}
  />
</Tabs.Root>
