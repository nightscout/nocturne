<script lang="ts">
  import * as Card from "$lib/components/ui/card";
  import { Link2Off } from "lucide-svelte";
</script>

<svelte:head>
  <title>Share link unavailable</title>
  <meta name="robots" content="noindex, nofollow" />
</svelte:head>

<div class="container mx-auto px-4 py-16 flex justify-center">
  <Card.Root class="w-full max-w-md">
    <Card.Header class="items-center text-center">
      <div class="mb-2 flex h-12 w-12 items-center justify-center rounded-full bg-muted">
        <Link2Off class="h-6 w-6 text-muted-foreground" />
      </div>
      <Card.Title>This share link isn't available</Card.Title>
    </Card.Header>
    <Card.Content class="text-center text-sm text-muted-foreground">
      <p>
        It may have been turned off or replaced with a new one. If someone was sharing their
        glucose data with you, ask them for the current link.
      </p>
    </Card.Content>
  </Card.Root>
</div>
