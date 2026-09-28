<script lang="ts">
  // Imported directly rather than through the barrel, which pulls the whole
  // component library in with it.
  import SidebarProvider from "$lib/components/ui/sidebar/sidebar-provider.svelte";
  import SidebarContextConsumer from "./SidebarContextConsumer.svelte";
</script>

<SidebarProvider>
  <SidebarContextConsumer />
</SidebarProvider>
