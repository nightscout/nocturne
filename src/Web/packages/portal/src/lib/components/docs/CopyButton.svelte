<script lang="ts">
    import { Copy, Check, X } from "@lucide/svelte";
    import { copyToClipboard } from "@nocturne/ui/utils";

    interface Props {
        text: string;
        label?: string;
    }

    let { text, label = "Copy to clipboard" }: Props = $props();

    let copied = $state(false);
    let failed = $state(false);
    let timer: ReturnType<typeof setTimeout> | undefined;

    async function copy() {
        if (!(await copyToClipboard(text))) {
            failed = true;
            return;
        }
        failed = false;
        copied = true;
        if (timer !== undefined) clearTimeout(timer);
        timer = setTimeout(() => {
            copied = false;
            timer = undefined;
        }, 2000);
    }
</script>

<button
    type="button"
    onclick={copy}
    class="shrink-0 rounded-md p-1.5 text-muted-foreground transition-colors hover:bg-background hover:text-foreground"
    aria-label={copied ? "Copied" : failed ? "Copy failed — select the text and copy it manually" : label}
>
    {#if copied}
        <Check class="h-4 w-4 text-green-500" />
    {:else if failed}
        <X class="h-4 w-4 text-destructive" />
    {:else}
        <Copy class="h-4 w-4" />
    {/if}
</button>
