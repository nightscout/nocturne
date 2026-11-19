<script lang="ts">
  import * as Tabs from "#lib/components/ui/tabs/index.js";
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "#lib/components/ui/card/index.js";
  import * as Alert from "#lib/components/ui/alert/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import RefreshCw from "@lucide/svelte/icons/refresh-cw";
  import * as Table from "#lib/components/ui/table/index.js";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import TriangleAlert from "@lucide/svelte/icons/triangle-alert";
  import type { JobLedgerEntry } from "#lib/api/index.js";
  import { formatMediumDateTime } from "#lib/utils/formatting.js";

  interface Props {
    jobs: JobLedgerEntry[];
    capped: boolean;
    loading: boolean;
    error: string | null;
    onRefresh: () => void;
  }

  let { jobs, capped, loading, error, onRefresh }: Props = $props();

  function formatTimestamp(value: string | undefined): string {
    return value ? formatMediumDateTime(value) : "None";
  }
</script>

<Tabs.Content value="jobs">
  <Card>
    <CardHeader class="flex flex-row items-center justify-between">
      <div>
        <CardTitle>Background jobs</CardTitle>
        <CardDescription>
          Each job and tenant has one row. Rows with a failed last run show first.
        </CardDescription>
      </div>
      <Button variant="outline" onclick={onRefresh} disabled={loading}>
        <RefreshCw class="h-4 w-4" />
        Refresh
      </Button>
    </CardHeader>
    <CardContent>
      {#if loading}
        <div class="flex items-center justify-center py-8">
          <Loader2 class="h-6 w-6 animate-spin text-muted-foreground" />
        </div>
      {:else if error}
        <Alert.Root variant="destructive">
          <TriangleAlert class="h-4 w-4" />
          <Alert.Description>{error}</Alert.Description>
        </Alert.Root>
      {:else if jobs.length === 0}
        <p class="py-8 text-center text-sm text-muted-foreground">No jobs are scheduled.</p>
      {:else}
        <p class="mb-2 text-sm text-muted-foreground">
          {#if capped}
            This list shows the first {jobs.length} rows only.
          {:else}
            This list shows all {jobs.length} rows.
          {/if}
        </p>
        <Table.Root>
          <Table.Header>
            <Table.Row>
              <Table.Head>Job</Table.Head>
              <Table.Head>Tenant</Table.Head>
              <Table.Head>Next run</Table.Head>
              <Table.Head>Last completed</Table.Head>
              <Table.Head>Attempts</Table.Head>
              <Table.Head>Last error</Table.Head>
            </Table.Row>
          </Table.Header>
          <Table.Body>
            {#each jobs as job (job.id)}
              {@const failing = (job.attempts ?? 0) > 0}
              <Table.Row>
                <Table.Cell class="font-medium">
                  <span class="flex items-center gap-2">
                    {#if failing}
                      <TriangleAlert class="h-4 w-4 shrink-0 text-destructive" aria-label="Failed" />
                    {/if}
                    {job.job}
                  </span>
                </Table.Cell>
                <Table.Cell class="font-mono">{job.tenantId ?? "Platform"}</Table.Cell>
                <Table.Cell>{formatTimestamp(job.nextDueAt)}</Table.Cell>
                <Table.Cell>{formatTimestamp(job.lastCompletedAt)}</Table.Cell>
                <Table.Cell>{job.attempts ?? 0}</Table.Cell>
                <Table.Cell variant="muted" class="max-w-xs whitespace-normal break-words">
                  {job.lastError ?? ""}
                </Table.Cell>
              </Table.Row>
            {/each}
          </Table.Body>
        </Table.Root>
      {/if}
    </CardContent>
  </Card>
</Tabs.Content>
