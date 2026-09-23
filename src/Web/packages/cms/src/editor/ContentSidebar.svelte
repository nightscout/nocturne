<script lang="ts">
  import { Button } from '@nocturne/ui/ui/button';
  import { Input } from '@nocturne/ui/ui/input';
  import { Badge } from '@nocturne/ui/ui/badge';
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
    <div class="relative">
      <Search class="absolute left-2 top-2.5 h-4 w-4 text-muted-foreground" />
      <Input
        placeholder="Search..."
        class="pl-8"
        bind:value={search}
      />
    </div>
  </div>

  <Separator />

  <div class="flex-1 overflow-y-auto">
    {#each filtered as item (item.id)}
      <button
        class="flex w-full items-start gap-3 p-3 text-left hover:bg-muted/50 transition-colors
          {selectedId === item.id ? 'bg-muted' : ''}"
        onclick={() => onSelect(item.id)}
      >
        <FileText class="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" />
        <div class="min-w-0 flex-1">
          <p class="truncate text-sm font-medium">{item.title || 'Untitled'}</p>
          <div class="mt-1 flex items-center gap-2">
            <Badge variant={item.status === 'published' ? 'default' : 'secondary'} class="text-xs">
              {item.status}
            </Badge>
            <span class="text-xs text-muted-foreground">{item.updatedAt}</span>
          </div>
        </div>
      </button>
    {/each}

    {#if filtered.length === 0}
      <p class="p-4 text-sm text-muted-foreground">No content found.</p>
    {/if}
  </div>
</div>
