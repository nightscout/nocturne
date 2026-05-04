<script lang="ts">
  import { onMount } from "svelte";
  import { goto } from "$app/navigation";
  import { getRules } from "$api/generated/alertRules.generated.remote";
  import type { AlertRuleResponse } from "$api-clients";
  import { Button } from "$lib/components/ui/button";
  import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
    CardDescription,
  } from "$lib/components/ui/card";
  import { ArrowLeft, PlayCircle } from "lucide-svelte";
  import ReplayPanel from "$lib/components/alerts/ReplayPanel.svelte";

  let rules = $state<AlertRuleResponse[]>([]);

  onMount(async () => {
    try {
      const r = await getRules();
      rules = Array.isArray(r) ? r : [];
    } catch {
      rules = [];
    }
  });
</script>

<svelte:head>
  <title>Alert simulator · Nocturne</title>
</svelte:head>

<div class="container mx-auto max-w-4xl p-4 lg:p-6 space-y-6">
  <div class="flex items-center gap-2">
    <Button
      type="button"
      variant="ghost"
      size="icon"
      onclick={() => goto("/alerts")}
      aria-label="Back to alerts"
    >
      <ArrowLeft class="h-4 w-4" />
    </Button>
    <div>
      <h1 class="text-2xl font-bold tracking-tight flex items-center gap-2">
        <PlayCircle class="h-5 w-5" /> Simulator
      </h1>
      <p class="text-sm text-muted-foreground">
        Replay your enabled rules against historical glucose. Nothing is delivered.
      </p>
    </div>
  </div>

  <Card>
    <CardHeader>
      <CardTitle class="text-base">Replay window</CardTitle>
      <CardDescription class="text-xs">
        Pick a window — rules are evaluated tick-by-tick over the data you actually had.
      </CardDescription>
    </CardHeader>
    <CardContent>
      <ReplayPanel availableRules={rules} />
    </CardContent>
  </Card>
</div>
