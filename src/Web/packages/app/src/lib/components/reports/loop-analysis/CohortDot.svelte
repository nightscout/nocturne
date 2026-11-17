<!-- A cohort's colour, as its bands are drawn, beside its name wherever the cohort is listed. -->
<script lang="ts">
  import { cn } from "#lib/utils/index.js";
  import { cohortBands } from "./event-response-view";

  let { index, class: className }: { index: number; class?: string } = $props();
</script>

<span
  class={cn("size-2.5 shrink-0 rounded-full bg-(--cohort)", className)}
  style:--cohort={cohortBands(index).median}
  aria-hidden="true"
></span>
