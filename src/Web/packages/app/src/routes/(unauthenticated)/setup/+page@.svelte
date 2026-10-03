<script lang="ts">
  import { page } from "$app/state";
  import SetupWizard from "./SetupWizard.svelte";
  import SetupHub from "./hub/SetupHub.svelte";
</script>

{#if page.data?.hub}
  <SetupHub />
{:else}
  <SetupWizard />
{/if}
