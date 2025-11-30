<script lang="ts">
  import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";

  import { page } from "$app/state";
</script>

<Card class="w-full max-w-2xl mx-auto mt-10">
  <CardHeader>
    <CardTitle class="text-2xl font-bold">{page.status} Error</CardTitle>
  </CardHeader>
  <CardContent>
    <p class="text-lg">An error occurred while processing your request.</p>
    {#if page.error}
      <p class="text-sm text-muted-foreground mt-2">{page.error.message}</p>
      {#if page.error.details}
        <p class="text-sm text-muted-foreground mt-2 p-2 bg-muted rounded">
          <strong>Details:</strong>
          {page.error.details}
        </p>
      {/if}
      {#if page.error.errorId}
        <p class="text-xs text-muted-foreground mt-4">
          Error ID: {page.error.errorId}
        </p>
      {/if}
    {/if}
  </CardContent>
</Card>
