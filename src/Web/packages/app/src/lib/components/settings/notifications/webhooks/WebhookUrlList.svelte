<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Trash2, Plus } from "lucide-svelte";

  interface Props {
    urls: string[];
    disabled?: boolean;
  }

  let { urls = $bindable([]), disabled = false }: Props = $props();

  function updateUrl(index: number, value: string) {
    urls = urls.map((url, idx) => (idx === index ? value : url));
  }

  function addUrl() {
    urls = [...urls, ""];
  }

  function removeUrl(index: number) {
    urls = urls.filter((_, idx) => idx !== index);
  }
</script>

<div class="space-y-2">
  {#if urls.length === 0}
    <div class="text-sm text-muted-foreground">No webhook URLs configured.</div>
  {/if}

  {#each urls as url, index}
    <div class="flex items-center gap-2">
      <Input
        value={url}
        placeholder="https://example.com/webhook"
        disabled={disabled}
        oninput={(e) =>
          updateUrl(index, (e.currentTarget as HTMLInputElement).value)
        }
      />
      <Button
        type="button"
        variant="ghost"
        size="icon"
        disabled={disabled}
        onclick={() => removeUrl(index)}
      >
        <Trash2 class="h-4 w-4" />
      </Button>
    </div>
  {/each}

  <Button
    type="button"
    variant="outline"
    class="gap-2"
    disabled={disabled}
    onclick={addUrl}
  >
    <Plus class="h-4 w-4" />
    Add URL
  </Button>
</div>
