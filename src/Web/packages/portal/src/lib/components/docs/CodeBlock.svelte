<script lang="ts">
    import CopyButton from "./CopyButton.svelte";

    interface Props {
        code: string;
        class?: string;
        maxHeight?: string;
    }

    let { code, class: className = "", maxHeight }: Props = $props();
</script>

<div class="relative group rounded-lg bg-muted/50 border border-border/60 {className}">
    <div class="absolute right-2 top-2 opacity-0 group-hover:opacity-100 transition-opacity">
        <CopyButton text={code} />
    </div>
    <pre
        class="p-3 text-sm overflow-x-auto"
        style={maxHeight ? `max-height: ${maxHeight}` : undefined}
    ><code>{code}</code></pre>
</div>
