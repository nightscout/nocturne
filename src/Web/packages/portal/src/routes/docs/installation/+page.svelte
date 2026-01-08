<script lang="ts">
    import { AlertCircle } from "@lucide/svelte";
</script>

<div class="max-w-3xl">
    <h1 class="text-4xl font-bold tracking-tight mb-4">Installation Guide</h1>
    <p class="text-lg text-muted-foreground mb-8">
        Detailed instructions for installing Nocturne on various platforms.
    </p>

    <div
        class="p-4 rounded-lg border border-amber-500/30 bg-amber-500/5 mb-8 not-prose"
    >
        <div class="flex items-start gap-3">
            <AlertCircle class="w-5 h-5 text-amber-500 mt-0.5 shrink-0" />
            <p class="text-sm text-muted-foreground">
                <strong class="text-amber-700 dark:text-amber-400">Placeholder content.</strong>
                This guide will be expanded with detailed instructions.
            </p>
        </div>
    </div>

    <h2 class="text-2xl font-bold mt-8 mb-4">System Requirements</h2>
    <div class="overflow-x-auto">
        <table class="w-full text-sm">
            <thead>
                <tr class="border-b border-border">
                    <th class="text-left py-2 pr-4 font-semibold">Component</th>
                    <th class="text-left py-2 pr-4 font-semibold">Minimum</th>
                    <th class="text-left py-2 font-semibold">Recommended</th>
                </tr>
            </thead>
            <tbody class="text-muted-foreground">
                <tr class="border-b border-border/50">
                    <td class="py-2 pr-4">RAM</td>
                    <td class="py-2 pr-4">1 GB</td>
                    <td class="py-2">2 GB+</td>
                </tr>
                <tr class="border-b border-border/50">
                    <td class="py-2 pr-4">Storage</td>
                    <td class="py-2 pr-4">10 GB</td>
                    <td class="py-2">20 GB+</td>
                </tr>
                <tr class="border-b border-border/50">
                    <td class="py-2 pr-4">Docker</td>
                    <td class="py-2 pr-4">20.10+</td>
                    <td class="py-2">Latest</td>
                </tr>
                <tr>
                    <td class="py-2 pr-4">Docker Compose</td>
                    <td class="py-2 pr-4">2.0+</td>
                    <td class="py-2">Latest</td>
                </tr>
            </tbody>
        </table>
    </div>

    <h2 class="text-2xl font-bold mt-8 mb-4">Docker Installation</h2>
    <p class="text-muted-foreground mb-4">
        The recommended way to run Nocturne is using Docker Compose:
    </p>
    <ol class="list-decimal list-inside space-y-4 text-muted-foreground">
        <li>
            <strong class="text-foreground">Generate configuration files</strong>
            <p class="ml-6 mt-1">
                Use the configuration wizard to generate your docker-compose.yml and
                environment files.
            </p>
        </li>
        <li>
            <strong class="text-foreground">Extract the downloaded ZIP</strong>
            <pre
                class="ml-6 mt-2 p-3 rounded-lg bg-muted/50 border border-border/60 text-sm"><code>unzip nocturne-config.zip
cd nocturne-config</code></pre>
        </li>
        <li>
            <strong class="text-foreground">Start the services</strong>
            <pre
                class="ml-6 mt-2 p-3 rounded-lg bg-muted/50 border border-border/60 text-sm"><code>docker compose up -d</code></pre>
        </li>
        <li>
            <strong class="text-foreground">Verify the installation</strong>
            <pre
                class="ml-6 mt-2 p-3 rounded-lg bg-muted/50 border border-border/60 text-sm"><code>docker compose ps
docker compose logs -f</code></pre>
        </li>
    </ol>

    <h2 class="text-2xl font-bold mt-8 mb-4">Platform-Specific Notes</h2>

    <h3 class="text-xl font-semibold mt-6 mb-3">Linux (Ubuntu/Debian)</h3>
    <p class="text-muted-foreground mb-4">
        Install Docker using the official repository for the latest version.
    </p>

    <h3 class="text-xl font-semibold mt-6 mb-3">Raspberry Pi</h3>
    <p class="text-muted-foreground mb-4">
        Nocturne supports ARM64 architecture. Use Raspberry Pi 4 or newer with 64-bit
        Raspberry Pi OS.
    </p>

    <h3 class="text-xl font-semibold mt-6 mb-3">Windows</h3>
    <p class="text-muted-foreground mb-4">
        Use Docker Desktop for Windows with WSL2 backend for best performance.
    </p>

    <h3 class="text-xl font-semibold mt-6 mb-3">macOS</h3>
    <p class="text-muted-foreground mb-4">
        Use Docker Desktop for Mac. Both Intel and Apple Silicon are supported.
    </p>

    <h2 class="text-2xl font-bold mt-8 mb-4">Troubleshooting</h2>
    <p class="text-muted-foreground">
        If you encounter issues during installation, check the logs with:
    </p>
    <pre
        class="mt-2 p-3 rounded-lg bg-muted/50 border border-border/60 text-sm"><code>docker compose logs</code></pre>
</div>
