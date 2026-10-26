<!-- A report's filters, as a report page adds them to the Filters panel. -->
<script lang="ts">
  import { Checkbox } from "#lib/components/ui/checkbox/index.js";
  import { useReportFilters } from "#lib/hooks/report-slots.svelte.js";

  let { log }: { log: string[] } = $props();

  useReportFilters({
    fields,
    begin: () => log.push("begin"),
    apply: (search) => {
      log.push("apply");
      return new URLSearchParams(search.toString());
    },
    reset: (search) => {
      log.push("reset");
      return new URLSearchParams(search.toString());
    },
  });
</script>

{#snippet fields()}
  <Checkbox aria-label="Report filter" />
{/snippet}
