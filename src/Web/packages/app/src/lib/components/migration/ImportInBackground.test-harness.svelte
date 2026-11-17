<script lang="ts">
  import { onMount } from "svelte";
  import type { MigrationJobState } from "#lib/api/index.js";
  import { createImportWatcher } from "#lib/migration/background-import.svelte.js";
  import ImportInBackgroundBanner from "./ImportInBackgroundBanner.svelte";
  import ImportInBackgroundNotice from "./ImportInBackgroundNotice.svelte";

  let { ended }: { ended?: (state: MigrationJobState) => void } = $props();

  // svelte-ignore state_referenced_locally
  const watcher = createImportWatcher({ ended });
  onMount(() => watcher.start());
</script>

<ImportInBackgroundBanner {watcher} />
<ImportInBackgroundNotice {watcher} />
