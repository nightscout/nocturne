<script lang="ts">
    import { Copy, Check } from "@lucide/svelte";

    interface Props {
        text: string;
        label?: string;
    }

    let { text, label = "Copy to clipboard" }: Props = $props();

    let copied = $state(false);
    let timer: ReturnType<typeof setTimeout> | undefined;

    async function copy() {
        await navigator.clipboard.writeText(text);
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
    aria-label={copied ? "Copied" : label}
>
    {#if copied}
        <Check class="h-4 w-4 text-green-500" />
    {:else}
        <Copy class="h-4 w-4" />
    {/if}
</button>
