<script lang="ts">
  import * as Tabs from "#lib/components/ui/tabs/index.js";
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "#lib/components/ui/card/index.js";
  import { EmptyState } from "#lib/components/shared/index.js";
  import { CompletionReason, type TrackerInstanceDto } from "#lib/api/index.js";

  interface Props {
    historyInstances: TrackerInstanceDto[];
    completionReasonLabels: Record<CompletionReason, string>;
    formatAge: (hours: number) => string;
    formatDate: (dateStr: Date | undefined | string) => string;
  }

  let {
    historyInstances,
    completionReasonLabels,
    formatAge,
    formatDate,
  }: Props = $props();
</script>

<Tabs.Content value="history">
  <Card data-testid="tracker-history">
    <CardHeader>
      <CardTitle>History</CardTitle>
      <CardDescription>Completed tracker instances</CardDescription>
    </CardHeader>
    <CardContent>
      {#if historyInstances.length === 0}
        <EmptyState art="clock" title="No history yet" />
      {:else}
        <div class="space-y-2">
          {#each historyInstances as instance (instance.id)}
            <div
              class="flex flex-col gap-1 p-3 rounded-lg border @md:flex-row @md:items-center @md:justify-between"
            >
              <div class="min-w-0">
                <div class="font-medium break-words">{instance.definitionName}</div>
                <div class="text-sm text-muted-foreground break-words">
                  {formatAge(instance.ageHours ?? 0)} ·
                  {completionReasonLabels[
                    instance.completionReason ??
                      CompletionReason.Completed
                  ]}
                  {#if instance.completionNotes}
                    · {instance.completionNotes}
                  {/if}
                </div>
              </div>
              <div class="shrink-0 text-sm text-muted-foreground">
                {formatDate(instance.completedAt ?? instance.startedAt)}
              </div>
            </div>
          {/each}
        </div>
      {/if}
    </CardContent>
  </Card>
</Tabs.Content>
