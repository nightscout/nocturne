<script lang="ts">
  import * as Card from "$lib/components/ui/card";
  import { Link2Off } from "lucide-svelte";
</script>

<svelte:head>
  <title>Share link not working</title>
  <meta name="robots" content="noindex, nofollow" />
</svelte:head>

<div class="container mx-auto px-4 py-16 flex justify-center">
  <Card.Root class="w-full max-w-md">
    <Card.Header class="items-center text-center">
      <div
        class="mb-2 flex h-12 w-12 items-center justify-center rounded-full bg-muted"
      >
        <Link2Off class="h-6 w-6 text-muted-foreground" />
      </div>
      <Card.Title>This share link isn't working</Card.Title>
    </Card.Header>
    <Card.Content class="space-y-3 text-center text-sm text-muted-foreground">
      <!-- A rotated link, a suspended tenant and an API that is briefly unready all land here and
           are indistinguishable from the browser, so the page names none of them. Prompting for a
           replacement link first would be actively harmful: rotating is how the owner would supply
           one, and it permanently breaks the link for every other recipient. -->
      <p>
        It may have been turned off or replaced, or the service may be
        temporarily unavailable.
      </p>
      <p>
        Try again in a few minutes. If it still doesn't work, ask the person who
        shared it to check the link.
      </p>
    </Card.Content>
  </Card.Root>
</div>
