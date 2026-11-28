<script lang="ts">
  import { Button } from "#lib/components/ui/button/index.js";
  import * as Popover from "#lib/components/ui/popover/index.js";
  import Plus from "@lucide/svelte/icons/plus";
  import {
    ELEMENT_GROUPS,
    ELEMENT_INFO,
    type ClockElementType,
  } from "#lib/clock-builder/index.js";
  import type { ClockPartAccessDto } from "#lib/api/index.js";
  import {
    canAdd,
    missingScopeFor,
    missingScopeReason,
  } from "#lib/components/clock/clock-face-data.svelte.js";

  interface Props {
    position: "top" | "bottom";
    open: boolean;
    /**
     * What the caller may add, undefined until it is read. An element the caller cannot add is
     * disabled with the reason; every element is disabled until it is read.
     */
    parts: ClockPartAccessDto[] | undefined;
    onOpenChange: (open: boolean) => void;
    onAddElement: (type: ClockElementType, position: "top" | "bottom") => void;
  }

  let { position, open, parts, onOpenChange, onAddElement }: Props = $props();

  /** The group's unavailable elements, by the reason they are unavailable. */
  function unavailable(types: ClockElementType[]): { reason: string; names: string }[] {
    if (parts === undefined) return [];
    const known = parts;
    const missing = types
      .map((type) => ({ type, scope: missingScopeFor(known, type) }))
      .filter((m): m is { type: ClockElementType; scope: string } => m.scope !== null);
    const scopes = missing.map((m) => m.scope).filter((scope, i, all) => all.indexOf(scope) === i);
    return scopes.map((scope) => ({
      reason: missingScopeReason(scope),
      names: missing
        .filter((m) => m.scope === scope)
        .map((m) => ELEMENT_INFO[m.type].name)
        .join(", "),
    }));
  }
</script>

<Popover.Root {open} {onOpenChange}>
  <Popover.Trigger class="w-full">
    <div
      class="flex h-8 w-full items-center justify-center rounded-lg border border-dashed border-white/20 text-white/30 transition-colors hover:border-white/40 hover:text-white/50"
    >
      <Plus class="size-4" />
    </div>
  </Popover.Trigger>
  <Popover.Content
    class="w-64"
    side={position === "top" ? "bottom" : "top"}
    align="center"
  >
    <div class="space-y-3">
      <h4 class="font-medium">Add element</h4>
      {#each ELEMENT_GROUPS as group (group.name)}
        <div>
          <p class="mb-1 text-xs font-medium text-muted-foreground">
            {group.name}
          </p>
          <div class="flex flex-wrap gap-1">
            {#each group.types as type (type)}
              <Button
                variant="outline"
                size="xs"
                disabled={!canAdd(parts, type)}
                onclick={() => onAddElement(type, position)}
              >
                {ELEMENT_INFO[type].name}
              </Button>
            {/each}
          </div>
          {#each unavailable(group.types) as { reason, names } (reason)}
            <p class="mt-1 text-xs text-muted-foreground">{names}: {reason}</p>
          {/each}
        </div>
      {/each}
    </div>
  </Popover.Content>
</Popover.Root>
