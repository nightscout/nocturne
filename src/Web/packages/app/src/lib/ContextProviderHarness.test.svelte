<script lang="ts">
  import { untrack } from "svelte";
  import Reader from "./ContextReaderHarness.test.svelte";

  let { provide, read }: { provide: () => unknown; read: () => unknown } = $props();
  untrack(() => provide());
</script>

<Reader {read} />
