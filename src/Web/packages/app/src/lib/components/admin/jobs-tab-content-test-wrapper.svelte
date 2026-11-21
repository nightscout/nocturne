<script lang="ts">
  import * as Tabs from "#lib/components/ui/tabs/index.js";
  import JobsTabContent from "./JobsTabContent.svelte";
  import type { JobLedgerEntry } from "@nocturne/api-client";

  interface Props {
    jobs: JobLedgerEntry[];
    capped?: boolean;
    loading: boolean;
    error: string | null;
  }

  let { jobs, capped = false, loading, error }: Props = $props();
</script>

<Tabs.Root value="jobs">
  <JobsTabContent {jobs} {capped} {loading} {error} onRefresh={() => {}} />
</Tabs.Root>
