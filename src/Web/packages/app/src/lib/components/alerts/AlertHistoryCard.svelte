<script lang="ts">
  import type { AlertHistoryResponse } from "$api-clients";
  import { AlertConditionType } from "$api-clients";
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import { Clock } from "lucide-svelte";
  import { formatElapsedDuration } from "$lib/utils/duration";

  interface Props {
    history: AlertHistoryResponse | null;
    page: number;
    loading: boolean;
    onLoadPage: (page: number) => void;
  }

  let { history, page, loading, onLoadPage }: Props = $props();

  function getConditionBadgeVariant(
    conditionType: AlertConditionType | undefined,
  ): "default" | "secondary" | "destructive" | "outline" {
    switch (conditionType) {
      case AlertConditionType.Threshold:
        return "destructive";
      case AlertConditionType.SignalLoss:
        return "secondary";
      default:
        return "outline";
    }
  }

  function getConditionLabel(conditionType: AlertConditionType | undefined): string {
    switch (conditionType) {
      case AlertConditionType.Threshold:
        return "Threshold";
      case AlertConditionType.RateOfChange:
        return "Rate of Change";
      case AlertConditionType.SignalLoss:
        return "Signal Loss";
      case AlertConditionType.Composite:
        return "Composite";
      default:
        return conditionType ?? "Unknown";
    }
  }

  function formatDate(date: Date | string | undefined): string {
    if (!date) return "-";
    const d = typeof date === "string" ? new Date(date) : date;
    return d.toLocaleString(undefined, {
      month: "short",
      day: "numeric",
      hour: "2-digit",
      minute: "2-digit",
    });
  }
</script>

<Card>
  <CardHeader>
    <CardTitle class="flex items-center gap-2">
      <Clock class="h-5 w-5" />
      Alert History
    </CardTitle>
    <CardDescription>
      Past alert excursions and their resolution
    </CardDescription>
  </CardHeader>
  <CardContent>
    {#if !history || !history.items || history.items.length === 0}
      <div class="text-center py-8 text-muted-foreground">
        <Clock class="h-12 w-12 mx-auto mb-4 opacity-50" />
        <p class="font-medium">No alert history</p>
        <p class="text-sm">
          Resolved alerts will appear here
        </p>
      </div>
    {:else}
      <div class="overflow-x-auto">
        <table class="w-full text-sm">
          <thead>
            <tr class="border-b">
              <th class="text-left py-2 pr-4 font-medium text-muted-foreground">Rule</th>
              <th class="text-left py-2 pr-4 font-medium text-muted-foreground">Type</th>
              <th class="text-left py-2 pr-4 font-medium text-muted-foreground">Started</th>
              <th class="text-left py-2 pr-4 font-medium text-muted-foreground">Duration</th>
              <th class="text-left py-2 font-medium text-muted-foreground">Acknowledged</th>
            </tr>
          </thead>
          <tbody>
            {#each history.items as item (item.id)}
              <tr class="border-b last:border-0">
                <td class="py-2 pr-4 font-medium">
                  {item.ruleName ?? "-"}
                </td>
                <td class="py-2 pr-4">
                  <Badge
                    variant={getConditionBadgeVariant(item.conditionType)}
                  >
                    {getConditionLabel(item.conditionType)}
                  </Badge>
                </td>
                <td class="py-2 pr-4 text-muted-foreground">
                  {formatDate(item.startedAt)}
                </td>
                <td class="py-2 pr-4 text-muted-foreground">
                  {formatElapsedDuration(item.startedAt, item.endedAt, "-")}
                </td>
                <td class="py-2 text-muted-foreground">
                  {#if item.acknowledgedAt}
                    {formatDate(item.acknowledgedAt)}
                    {#if item.acknowledgedBy}
                      <span class="text-xs ml-1">
                        by {item.acknowledgedBy}
                      </span>
                    {/if}
                  {:else}
                    <span class="text-muted-foreground/50">-</span>
                  {/if}
                </td>
              </tr>
            {/each}
          </tbody>
        </table>
      </div>

      <!-- Pagination -->
      {#if (history.totalPages ?? 1) > 1}
        <div class="flex items-center justify-between mt-4 pt-4 border-t">
          <p class="text-sm text-muted-foreground">
            Page {history.page ?? 1} of {history.totalPages ?? 1}
            ({history.totalCount ?? 0} total)
          </p>
          <div class="flex gap-2">
            <Button
              variant="outline"
              size="sm"
              disabled={page <= 1 || loading}
              onclick={() => onLoadPage(page - 1)}
            >
              Previous
            </Button>
            <Button
              variant="outline"
              size="sm"
              disabled={page >= (history.totalPages ?? 1) || loading}
              onclick={() => onLoadPage(page + 1)}
            >
              Next
            </Button>
          </div>
        </div>
      {/if}
    {/if}
  </CardContent>
</Card>
