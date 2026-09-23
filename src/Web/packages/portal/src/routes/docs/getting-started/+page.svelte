<script lang="ts">
    import Content from "../../../content/docs/getting-started.svx";
    import { Button } from "@nocturne/ui/ui/button";
    import { ArrowRight } from "@lucide/svelte";
</script>

<div class="max-w-3xl">
    <Content />

    <div class="mt-12 flex gap-4 not-prose">
        <Button href="/docs/installation" variant="outline">
            Full Installation Guide
            <ArrowRight class="w-4 h-4" />
        </Button>
    </div>
</div>
