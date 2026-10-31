<script lang="ts">
  import { resolve } from "$app/paths";
  import { Button } from "#lib/components/ui/button/index.js";
  import { ConfirmDialog } from "#lib/components/ui/confirm-dialog/index.js";
  import * as Table from "#lib/components/ui/table/index.js";
  import {
    deleteCustomReport,
    getCustomReports,
  } from "#lib/api/generated/customReports.generated.remote.js";
  import type { CustomReportSummary } from "#lib/api/generated/nocturne-api-client.js";
  import { contextResource } from "#lib/hooks/resource-context.svelte.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import { formatDateTime, formatNumber } from "#lib/utils/formatting.js";
  import LayoutDashboard from "@lucide/svelte/icons/layout-dashboard";
  import Plus from "@lucide/svelte/icons/plus";
  import Trash2 from "@lucide/svelte/icons/trash-2";

  const resource = contextResource(() => getCustomReports(), {
    errorTitle: "Could not load the custom reports",
  });

  const reports = $derived<CustomReportSummary[]>(resource.current ?? []);

  let pendingDelete = $state<CustomReportSummary | null>(null);
  let deleting = $state(false);
  let deleteError = $state<string | null>(null);

  async function confirmDelete() {
    if (!pendingDelete?.id) return;
    deleting = true;
    deleteError = null;
    try {
      await deleteCustomReport(pendingDelete.id);
      pendingDelete = null;
    } catch (err) {
      deleteError = describeSubmitError(err, "Could not delete the report. Try again.");
    } finally {
      deleting = false;
    }
  }
</script>

<div class="@container space-y-6">
  <div class="flex flex-wrap items-start gap-3">
    <div class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-primary/10">
      <LayoutDashboard class="h-5 w-5 text-primary" aria-hidden="true" />
    </div>
    <div class="space-y-1">
      <h2 class="text-xl font-semibold">Saved custom reports</h2>
      <p class="text-sm text-muted-foreground">
        A custom report shows the charts and figures of your data that you select. Each member of this
        account sees the same list.
      </p>
    </div>
    <Button class="@lg:ml-auto" href={resolve("/(authenticated)/reports/custom/new")}>
      <Plus aria-hidden="true" />
      New report
    </Button>
  </div>

  {#if deleteError}
    <p class="text-sm text-destructive" role="alert">{deleteError}</p>
  {/if}

  {#if resource.current}
    {#if reports.length === 0}
      <p class="text-sm text-muted-foreground">
        No custom reports. To make a report, select New report.
      </p>
    {:else}
      <Table.Root>
        <Table.Header>
          <Table.Row>
            <Table.Head>Name</Table.Head>
            <Table.Head>Last changed</Table.Head>
            <Table.Head>Panels</Table.Head>
            <Table.Head><span class="sr-only">Actions</span></Table.Head>
          </Table.Row>
        </Table.Header>
        <Table.Body>
          {#each reports as report (report.id)}
            <Table.Row>
              <Table.Cell class="font-medium">
                <a href={resolve(`reports/custom/${report.id}`)} class="hover:underline">{report.name}</a>
              </Table.Cell>
              <Table.Cell class="whitespace-nowrap tabular-nums">{formatDateTime(report.updatedAt)}</Table.Cell>
              <Table.Cell class="tabular-nums">{formatNumber(report.panelCount ?? 0)}</Table.Cell>
              <Table.Cell class="text-right">
                {#if report.canEdit}
                  <Button
                    variant="ghost"
                    size="sm"
                    aria-label="Delete {report.name}"
                    onclick={() => (pendingDelete = report)}
                  >
                    <Trash2 class="size-4" aria-hidden="true" />
                    Delete
                  </Button>
                {/if}
              </Table.Cell>
            </Table.Row>
          {/each}
        </Table.Body>
      </Table.Root>
    {/if}
  {/if}
</div>

<ConfirmDialog
  open={pendingDelete !== null}
  onOpenChange={(open) => {
    if (!open) pendingDelete = null;
  }}
  title="Delete this custom report?"
  confirmLabel="Delete"
  destructive
  busy={deleting}
  onConfirm={confirmDelete}
>
  {#snippet description()}
    Nocturne will delete "{pendingDelete?.name}" for every member of this account. Your data stays.
  {/snippet}
</ConfirmDialog>
