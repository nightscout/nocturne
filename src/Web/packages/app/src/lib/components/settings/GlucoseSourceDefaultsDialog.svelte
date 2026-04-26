<script lang="ts">
  import * as Dialog from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import {
    Select,
    SelectContent,
    SelectItem,
    SelectTrigger,
  } from "$lib/components/ui/select";
  import { GripVertical, Plus, Trash2 } from "lucide-svelte";

  interface Rule {
    match: string;
    field: string;
    processing: string;
  }

  interface Props {
    open: boolean;
    rules: Rule[];
    onSave: (rules: Rule[]) => void;
    onCancel: () => void;
  }

  let { open = $bindable(), rules, onSave, onCancel }: Props = $props();

  let localRules = $state<Rule[]>([]);
  let dragIndex = $state<number | null>(null);
  let dropIndex = $state<number | null>(null);

  // Clone rules into local state when dialog opens
  $effect(() => {
    if (open) {
      localRules = rules.map((r) => ({ ...r }));
    }
  });

  function addRule() {
    localRules = [...localRules, { match: "", field: "device", processing: "Smoothed" }];
  }

  function removeRule(index: number) {
    localRules = localRules.filter((_, i) => i !== index);
  }

  function handleSave() {
    onSave(localRules);
  }
</script>

<Dialog.Root bind:open>
  <Dialog.Content class="sm:max-w-[640px]">
    <Dialog.Header>
      <Dialog.Title>Source Default Rules</Dialog.Title>
      <Dialog.Description>
        Configure glucose processing defaults per uploading client. Rules are evaluated top-to-bottom; first match wins.
      </Dialog.Description>
    </Dialog.Header>

    <div class="space-y-2 py-4">
      {#if localRules.length === 0}
        <div class="text-center py-8 text-muted-foreground border border-dashed rounded-lg">
          <p class="text-sm">No rules configured.</p>
          <p class="text-xs">Add a rule to set processing defaults based on the uploading client.</p>
        </div>
      {:else}
        {#each localRules as rule, index (index)}
          <div
            role="listitem"
            draggable="true"
            ondragstart={(e) => {
              dragIndex = index;
              e.dataTransfer?.setData("text/plain", String(index));
            }}
            ondragover={(e) => {
              e.preventDefault();
              dropIndex = index;
            }}
            ondrop={(e) => {
              e.preventDefault();
              if (dragIndex !== null && dragIndex !== index) {
                const updated = [...localRules];
                const [item] = updated.splice(dragIndex, 1);
                updated.splice(index, 0, item);
                localRules = updated;
              }
              dragIndex = null;
              dropIndex = null;
            }}
            ondragend={() => {
              dragIndex = null;
              dropIndex = null;
            }}
            class="flex items-center gap-2 rounded-lg border p-2 transition-all {dropIndex === index
              ? 'border-primary bg-accent'
              : 'border-border'} {dragIndex === index ? 'opacity-50' : ''}"
          >
            <GripVertical class="h-4 w-4 shrink-0 text-muted-foreground cursor-grab" />

            <div class="w-[120px] shrink-0">
              <Select
                type="single"
                value={rule.field}
                onValueChange={(value) => {
                  localRules[index].field = value;
                }}
              >
                <SelectTrigger>
                  <span>{rule.field === "app" ? "App" : "Device"}</span>
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="device">Device</SelectItem>
                  <SelectItem value="app">App</SelectItem>
                </SelectContent>
              </Select>
            </div>

            <div class="flex-1 min-w-0">
              <Input
                placeholder="Prefix to match..."
                value={rule.match}
                oninput={(e) => {
                  localRules[index].match = e.currentTarget.value;
                }}
              />
            </div>

            <div class="w-[150px] shrink-0">
              <Select
                type="single"
                value={rule.processing}
                onValueChange={(value) => {
                  localRules[index].processing = value;
                }}
              >
                <SelectTrigger>
                  <span>{rule.processing}</span>
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="Smoothed">Smoothed</SelectItem>
                  <SelectItem value="Unsmoothed">Unsmoothed</SelectItem>
                </SelectContent>
              </Select>
            </div>

            <Button
              variant="ghost"
              size="sm"
              class="h-8 w-8 shrink-0 p-0 text-muted-foreground hover:text-destructive"
              onclick={() => removeRule(index)}
            >
              <Trash2 class="h-4 w-4" />
            </Button>
          </div>
        {/each}
      {/if}

      <Button variant="outline" size="sm" class="w-full" onclick={addRule}>
        <Plus class="h-4 w-4 mr-2" />
        Add rule
      </Button>
    </div>

    <Dialog.Footer>
      <Button variant="outline" onclick={onCancel}>Cancel</Button>
      <Button onclick={handleSave}>Save</Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>
