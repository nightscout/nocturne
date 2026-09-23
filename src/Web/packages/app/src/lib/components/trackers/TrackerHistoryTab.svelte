<script lang="ts">
  import * as Tabs from "$lib/components/ui/tabs";
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { History } from "lucide-svelte";
  import { CompletionReason, type TrackerInstanceDto } from "$api";

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
  <Card>
    <CardHeader>
      <CardTitle>History</CardTitle>
      <CardDescription>Completed tracker instances</CardDescription>
    </CardHeader>
    <CardContent>
      {#if historyInstances.length === 0}
        <div class="text-center py-8 text-muted-foreground">
          <History class="h-12 w-12 mx-auto mb-3 opacity-50" />
          <p>No history yet</p>
        </div>
      {:else}
        <div class="space-y-2">
          {#each historyInstances as instance (instance.id)}
            <div
              class="flex items-center justify-between p-3 rounded-lg border"
            >
              <div>
                <div class="font-medium">{instance.definitionName}</div>
                <div class="text-sm text-muted-foreground">
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
              <div class="text-sm text-muted-foreground">
                {formatDate(instance.completedAt ?? instance.startedAt)}
              </div>
            </div>
          {/each}
        </div>
      {/if}
    </CardContent>
  </Card>
</Tabs.Content>
