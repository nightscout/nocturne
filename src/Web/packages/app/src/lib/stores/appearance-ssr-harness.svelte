<script lang="ts">
  // Test-only harness: stands in for the root layout, publishing one request's
  // preference layers so a descendant can be rendered server-side.
  import { setPreferencesContext } from "./appearance-store.svelte";
  import Reader from "./appearance-ssr-reader.svelte";
  import type { UserDisplayPreferences } from "$lib/api";

  let { layers }: { layers: UserDisplayPreferences[] } = $props();

  setPreferencesContext(() => layers);
</script>

<Reader />
