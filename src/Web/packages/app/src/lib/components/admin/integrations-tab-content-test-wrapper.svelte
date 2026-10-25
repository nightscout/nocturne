<script lang="ts">
  import * as Tabs from "$lib/components/ui/tabs";
  import IntegrationsTabContent from "./IntegrationsTabContent.svelte";
  import type { ComponentProps } from "svelte";
  let componentProps: ComponentProps<typeof IntegrationsTabContent> = $props();
</script>

<Tabs.Root value="integrations">
  <IntegrationsTabContent {...componentProps} />
</Tabs.Root>
