<script lang="ts">
  import { untrack, type ComponentProps } from "svelte";
  import type { FeatureSettings } from "$lib/api";
  import { createSettingsStore } from "$lib/stores/settings-store.svelte";
  import Page from "./+page.svelte";

  const {
    features,
    data,
  }: {
    features: FeatureSettings;
    data: ComponentProps<typeof Page>["data"];
  } = $props();

  // The page reads tenant settings out of context; the layout owns the real one.
  const store = createSettingsStore(false);
  untrack(() => {
    store.features = features;
  });
</script>

<Page {data} />
