<!-- The form builder with a page's one action, Save, which hands the spec to the test. -->
<script lang="ts">
  import type { CustomReportCatalogue } from "#lib/api/generated/nocturne-api-client.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import ReportBuilder from "./ReportBuilder.svelte";

  interface Props {
    catalogue: CustomReportCatalogue;
    spec?: unknown;
    onsave: (spec: Record<string, unknown>) => void;
  }

  let { catalogue, spec, onsave }: Props = $props();
</script>

<ReportBuilder {catalogue} {spec}>
  {#snippet actions({ spec: current, empty })}
    <Button disabled={empty} onclick={() => onsave(current())}>Save</Button>
  {/snippet}
</ReportBuilder>
