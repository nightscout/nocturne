<script>
  import { page } from "$app/state";
  import { params } from "virtual:feature";
  import { fixture } from "virtual:report";
  import { tryGetRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import * as Tooltip from "$lib/components/ui/tooltip";

  let { children } = $props();

  // Pages read their route parameters from `page.params`, which the capture resolved from the tenant.
  Object.assign(page.params, params);

  // Live surfaces (a clock face, the tracker pills) read the latest readings from the realtime
  // store, which never connects here; the capture recorded the readings it would have had.
  const store = tryGetRealtimeStore();
  if (store && fixture.entries) store.entries = fixture.entries;
</script>

<!-- The app's root layout provides tooltips; a feature page's help icons need the provider. -->
<Tooltip.Provider>
  <div data-report-root class="flex flex-col gap-12 p-8">
    {@render children()}
  </div>
</Tooltip.Provider>
