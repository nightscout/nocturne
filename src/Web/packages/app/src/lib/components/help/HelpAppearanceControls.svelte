<script lang="ts">
  import { Slider } from "$lib/components/ui/slider";
  import { Button } from "$lib/components/ui/button";
  import { getHelpContext } from "$lib/help/help-context.svelte";
  const help = getHelpContext();
</script>

{#if help}
  <div class="space-y-4">
    <div class="space-y-3">
      <p class="flex justify-between gap-3 text-sm font-medium">
        Transparency <span>{help.transparency}%</span>
      </p>
      <Slider
        type="single"
        thumbLabel="Documentation transparency"
        min={0}
        max={100}
        step={1}
        value={help.transparency}
        onValueChange={(transparency) =>
          help.setAppearance({ transparency, blur: help.blur })}
      />
    </div>
    <div class="space-y-3">
      <p class="flex justify-between gap-3 text-sm font-medium">
        Background blur <span>{help.blur} px</span>
      </p>
      <Slider
        type="single"
        thumbLabel="Documentation background blur"
        min={0}
        max={20}
        step={1}
        value={help.blur}
        onValueChange={(blur) =>
          help.setAppearance({ transparency: help.transparency, blur })}
      />
    </div>
    <p class="text-xs text-muted-foreground">
      Only the background layer changes. Documentation text stays fully visible.
      Saved in this browser.
    </p>
    <Button
      variant="outline"
      size="sm"
      onclick={() => help.setAppearance({ transparency: 22, blur: 2 })}
    >
      Reset help appearance
    </Button>
  </div>
{/if}
