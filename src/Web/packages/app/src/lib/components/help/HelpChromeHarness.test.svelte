<script lang="ts">
  import { createHelpContext } from "$lib/help/help-context.svelte";
  import HelpBar from "./HelpBar.svelte";
  import HelpIcon from "./HelpIcon.svelte";
  createHelpContext();
</script>

<HelpBar />
<HelpIcon />
