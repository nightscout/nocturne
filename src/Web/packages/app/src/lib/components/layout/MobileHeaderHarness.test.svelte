<script lang="ts">
  import * as Sidebar from "#lib/components/ui/sidebar/index.js";
  import MobileHeader from "./MobileHeader.svelte";
</script>

<!-- The sidebar trigger and the value indicator's tooltip need the provider the app layout supplies. -->
<Sidebar.Provider>
  <MobileHeader />
</Sidebar.Provider>
