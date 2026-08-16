<script lang="ts">
  // Test-only harness: stands in for the root layout, publishing one request's
  // preference sources so a descendant can be rendered server-side.
  import { setPreferencesContext, type RequestPreferences } from "./appearance-store.svelte";
  import Reader from "./appearance-ssr-reader.svelte";

  let { layers, language }: RequestPreferences = $props();

  setPreferencesContext(() => ({ layers, language }));
</script>

<Reader />
