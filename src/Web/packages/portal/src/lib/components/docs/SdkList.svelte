<script lang="ts">
    // Renders the supported-SDK table. The rows come from SDKS, which is derived
    // from sdk/<lang>/config.yaml at build time — adding or removing an SDK there
    // updates this table with no edit here.
    import { SDKS } from "$lib/sdk/sdks";
</script>

<table>
    <thead>
        <tr>
            <th>Language</th>
            <th>Package</th>
            <th>Install</th>
        </tr>
    </thead>
    <tbody>
        {#each SDKS as sdk (sdk.dir)}
            <tr>
                <td>{sdk.name}</td>
                <td>
                    <a href={sdk.registryUrl} target="_blank" rel="noreferrer">
                        <code>{sdk.package}</code>
                    </a>
                    <br />
                    <span class="text-sm text-muted-foreground">{sdk.registry}</span>
                </td>
                <td><code>{sdk.install}</code></td>
            </tr>
        {/each}
    </tbody>
</table>
