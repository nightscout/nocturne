<script lang="ts">
  import { Input } from '@nocturne/ui/ui/input';
  import { Textarea } from '@nocturne/ui/ui/textarea';
  import { Switch } from '@nocturne/ui/ui/switch';
  import { Label } from '@nocturne/ui/ui/label';
  import { ChevronDown, ChevronUp } from '@lucide/svelte';
  import type { MetadataField } from './types.ts';

  let {
    fields,
    metadata = $bindable({}),
  }: {
    fields: MetadataField[];
    metadata: Record<string, unknown>;
  } = $props();

  let collapsed = $state(false);
</script>

<div class="border-b border-border/40">
  <button
    class="flex w-full items-center justify-between px-4 py-2 text-sm font-medium hover:bg-muted/50"
    onclick={() => (collapsed = !collapsed)}
  >
    Metadata
    {#if collapsed}
      <ChevronDown class="h-4 w-4" />
    {:else}
      <ChevronUp class="h-4 w-4" />
    {/if}
  </button>

  {#if !collapsed}
    <div class="space-y-3 px-4 pb-4">
      {#each fields as field (field.key)}
        <div class="space-y-1">
          <Label for={field.key}>{field.label}{field.required ? ' *' : ''}</Label>
          {#if field.type === 'text'}
            <Input
              id={field.key}
              value={String(metadata[field.key] ?? '')}
              placeholder={field.placeholder}
              oninput={(e) => (metadata[field.key] = e.currentTarget.value)}
            />
          {:else if field.type === 'textarea'}
            <Textarea
              id={field.key}
              value={String(metadata[field.key] ?? '')}
              placeholder={field.placeholder}
              oninput={(e) => (metadata[field.key] = e.currentTarget.value)}
            />
          {:else if field.type === 'date'}
            <Input
              id={field.key}
              type="date"
              value={String(metadata[field.key] ?? '')}
              oninput={(e) => (metadata[field.key] = e.currentTarget.value)}
            />
          {:else if field.type === 'toggle'}
            <Switch
              checked={Boolean(metadata[field.key])}
              onCheckedChange={(checked) => (metadata[field.key] = checked)}
            />
          {:else if field.type === 'tags'}
            {@const tags = metadata[field.key]}
            <Input
              id={field.key}
              value={Array.isArray(tags) ? tags.join(', ') : ''}
              placeholder="tag1, tag2, tag3"
              oninput={(e) => (metadata[field.key] = e.currentTarget.value.split(',').map((s) => s.trim()).filter(Boolean))}
            />
          {/if}
        </div>
      {/each}
    </div>
  {/if}
</div>
