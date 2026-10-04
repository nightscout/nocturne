<script lang="ts">
  import { createReportPrintContext } from "$lib/components/reports/print/report-print.svelte";
  import YearOverview from "./+page.svelte";
  import { Button } from "$lib/components/ui/button";
  const print = createReportPrintContext();
  let { settle }: { settle?: () => Promise<void> } = $props();
  let prepared = $state(false);
  async function prepare() {
    print.printing = true;
    try {
      await print.prepare();
      await settle?.();
      prepared = true;
    } finally {
      print.printing = false;
    }
  }
</script>

<YearOverview />
<Button variant="outline" size="sm" onclick={prepare}>Prepare print</Button>
<output data-testid="print-ready">{prepared}</output>
