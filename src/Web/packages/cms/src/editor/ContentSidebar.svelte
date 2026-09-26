<script lang="ts">
  import { Button } from '@nocturne/ui/ui/button';
  import * as InputGroup from '@nocturne/ui/ui/input-group';
  import { Badge } from '@nocturne/ui/ui/badge';
  import * as Item from '@nocturne/ui/ui/item';
  import { Separator } from '@nocturne/ui/ui/separator';
  import { Plus, Search, FileText } from '@lucide/svelte';
  import type { ContentItem } from './types.ts';

  let {
    items,
    selectedId,
    onSelect,
    onCreate,
    label = 'Content',
  }: {
    items: ContentItem[];
    selectedId?: string;
    onSelect: (id: string) => void;
    onCreate: () => void;
    label?: string;
  } = $props();

  let search = $state('');

  const filtered = $derived(
    items.filter((item) =>
      item.title.toLowerCase().includes(search.toLowerCase()),
    ),
  );
</script>

<div class="sticky top-0 flex h-screen w-64 flex-col border-r border-border/40">
  <div class="flex items-center justify-between p-4">
    <h2 class="text-sm font-semibold">{label}</h2>
    <Button variant="ghost" size="icon" onclick={onCreate}>
      <Plus class="h-4 w-4" />
    </Button>
  </div>

  <div class="px-4 pb-2">
    <InputGroup.Root>
      <InputGroup.Addon>
        <Search />
      </InputGroup.Addon>
      <InputGroup.Input placeholder="Search..." bind:value={search} />
    </InputGroup.Root>
  </div>

  <Separator />

  <div class="flex flex-1 flex-col gap-1 overflow-y-auto p-2">
    {#each filtered as item (item.id)}
      <Item.Root
        variant="ghost"
        size="sm"
        class="items-start"
        aria-current={selectedId === item.id ? 'true' : undefined}
        onclick={() => onSelect(item.id)}
      >
        <Item.Media>
          <FileText class="mt-0.5 size-4 text-muted-foreground" />
        </Item.Media>
        <Item.Content class="min-w-0">
          <p class="truncate text-sm font-medium">{item.title || 'Untitled'}</p>
          <div class="mt-1 flex items-center gap-2">
            <Badge variant={item.status === 'published' ? 'default' : 'secondary'}>
              {item.status}
            </Badge>
            <Item.Description>{item.updatedAt}</Item.Description>
          </div>
        </Item.Content>
      </Item.Root>
    {/each}

    {#if filtered.length === 0}
      <p class="p-4 text-sm text-muted-foreground">No content found.</p>
    {/if}
  </div>
</div>
