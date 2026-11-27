<script lang="ts">
  import { resolve } from "$app/paths";
  import FlagRulesEditor from "#lib/components/people/FlagRulesEditor.svelte";
  import { Button } from "#lib/components/ui/button/index.js";
  import * as Card from "#lib/components/ui/card/index.js";
  import ArrowLeft from "@lucide/svelte/icons/arrow-left";
  import Flag from "@lucide/svelte/icons/flag";
</script>

<svelte:head>
  <title>Your flag rules - Nocturne</title>
</svelte:head>

<div class="container mx-auto max-w-3xl space-y-6 p-4 md:p-6">
  <div class="space-y-2">
    <Button variant="ghost" size="sm" href={resolve("/(authenticated)/people")}>
      <ArrowLeft class="size-4" aria-hidden="true" />
      People
    </Button>
    <div class="flex items-center gap-3">
      <div class="flex size-12 shrink-0 items-center justify-center rounded-xl bg-primary/10">
        <Flag class="size-6 text-primary" aria-hidden="true" />
      </div>
      <div>
        <h1 class="text-2xl font-bold">Your flag rules</h1>
        <p class="text-sm text-muted-foreground">
          A person who meets one of your rules shows in Flagged on the People page. Only you see your rules.
        </p>
      </div>
    </div>
  </div>

  <Card.Root>
    <Card.Content>
      <FlagRulesEditor owner={{ kind: "personal" }} canEdit />
    </Card.Content>
  </Card.Root>
</div>
