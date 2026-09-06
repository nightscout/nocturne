<script lang="ts">
  import type { ComponentType, Snippet } from "svelte";
  import * as Card from "$lib/components/ui/card";

  interface Props {
    icon: ComponentType;
    title: string;
    children: Snippet;
  }

  const { icon: Icon, title, children }: Props = $props();
</script>

<div class="container mx-auto px-4 py-16 flex justify-center">
  <Card.Root class="w-full max-w-md">
    <Card.Header class="items-center text-center">
      <div class="mb-2 flex h-12 w-12 items-center justify-center rounded-full bg-muted">
        <Icon class="h-6 w-6 text-muted-foreground" />
      </div>
      <Card.Title>{title}</Card.Title>
    </Card.Header>
    <Card.Content class="space-y-3 text-center text-sm text-muted-foreground">
      {@render children()}
    </Card.Content>
  </Card.Root>
</div>
