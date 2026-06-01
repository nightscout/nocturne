<script lang="ts">
    import { ExternalLink } from "@lucide/svelte";
</script>

<a
    href="https://feedback.pikapods.com/posts/1118/nocturne-diabetes-management-platform"
    target="_blank"
    rel="noopener noreferrer"
    class="p-6 rounded-xl border border-dashed border-border/60 bg-card/30 hover:bg-card/50 hover:border-primary/30 transition-colors group"
>
    <div class="flex items-start gap-4">
        <div class="w-12 h-12 rounded-lg bg-violet-500/15 flex items-center justify-center shrink-0">
            <img
                src="/logos/pikapods.png"
                alt="PikaPods"
                class="w-7 h-7 object-contain"
            />
        </div>
        <div class="flex-1">
            <h3 class="text-lg font-semibold mb-1 group-hover:text-primary transition-colors">
                PikaPods
            </h3>
            <p class="text-sm text-muted-foreground">
                Vote to add Nocturne to PikaPods, a low-cost managed hosting platform for open-source apps. The more votes, the sooner it gets added.
            </p>
        </div>
        <ExternalLink class="w-5 h-5 text-muted-foreground group-hover:text-primary transition-colors mt-1" />
    </div>
</a>
