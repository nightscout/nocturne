<script lang="ts">
    import { Github, Heart } from "@lucide/svelte";
    import LanguageSelector from "./LanguageSelector.svelte";
</script>

<footer class="border-t border-border/40 bg-muted/30">
    <div class="container mx-auto px-4 py-12">
        <div class="grid grid-cols-1 md:grid-cols-4 gap-8">
            <!-- Brand -->
            <div class="md:col-span-1">
                <a href="/" class="flex items-center gap-2.5 mb-4">
                    <div
                        class="w-8 h-8 rounded-lg bg-primary/20 flex items-center justify-center"
                    >
                        <span class="text-primary font-bold">N</span>
                    </div>
                    <span class="text-lg font-semibold">Nocturne</span>
                </a>
                <p class="text-sm text-muted-foreground">
                    Open source diabetes data management with full Nightscout
                    compatibility.
                </p>
            </div>

            <!-- Product -->
            <div>
                <h3 class="font-semibold mb-4">Product</h3>
                <ul class="space-y-2.5">
                    <li>
                        <a
                            href="/features"
                            class="text-sm text-muted-foreground hover:text-foreground transition-colors"
                        >
                            Features
                        </a>
                    </li>
                    <li>
                        <a
                            href="/setup"
                            class="text-sm text-muted-foreground hover:text-foreground transition-colors"
                        >
                            Get Started
                        </a>
                    </li>
                    <li>
                        <a
                            href="/faq"
                            class="text-sm text-muted-foreground hover:text-foreground transition-colors"
                        >
                            FAQ
                        </a>
                    </li>
                </ul>
            </div>

            <!-- Documentation -->
            <div>
                <h3 class="font-semibold mb-4">Documentation</h3>
                <ul class="space-y-2.5">
                    <li>
                        <a
                            href="/docs/getting-started"
                            class="text-sm text-muted-foreground hover:text-foreground transition-colors"
                        >
                            Getting Started
                        </a>
                    </li>
                    <li>
                        <a
                            href="/docs/installation"
                            class="text-sm text-muted-foreground hover:text-foreground transition-colors"
                        >
                            Installation
                        </a>
                    </li>
                    <li>
                        <a
                            href="/docs/configuration"
                            class="text-sm text-muted-foreground hover:text-foreground transition-colors"
                        >
                            Configuration
                        </a>
                    </li>
                </ul>
            </div>

            <!-- Community -->
            <div>
                <h3 class="font-semibold mb-4">Community</h3>
                <ul class="space-y-2.5">
                    <li>
                        <a
                            href="https://github.com/nightscout/nocturne"
                            target="_blank"
                            rel="noopener noreferrer"
                            class="text-sm text-muted-foreground hover:text-foreground transition-colors inline-flex items-center gap-1.5"
                        >
                            <Github class="w-4 h-4" />
                            GitHub
                        </a>
                    </li>
                </ul>
            </div>
        </div>

        <!-- Bottom Bar -->
        <div
            class="mt-10 pt-6 border-t border-border/40 flex flex-col md:flex-row justify-between items-center gap-4"
        >
            <p class="text-sm text-muted-foreground">
                &copy; {new Date().getFullYear()} Nocturne. Open source under MIT
                license.
            </p>
            <div class="flex items-center gap-4">
                <LanguageSelector compact />
                <p class="text-sm text-muted-foreground flex items-center gap-1.5">
                    Made with <Heart class="w-4 h-4 text-red-500 fill-red-500" /> by the
                    diabetes community
                </p>
            </div>
        </div>
    </div>
</footer>
