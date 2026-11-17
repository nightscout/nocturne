<script>
  import { Layout, Page } from "virtual:report";
  import { createRealtimeStore } from "$lib/stores/realtime-store.svelte";

  // The authenticated app layout provides this; created without `initialize()` it never connects,
  // and the pages here read their data from the fixture.
  createRealtimeStore({ url: "" });
</script>

<Layout><Page /></Layout>
