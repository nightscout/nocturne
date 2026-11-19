<script lang="ts">
  import { scopesOf } from '../spec';
  import SpecBlock from './SpecBlock.svelte';
</script>

<SpecBlock rows={scopesOf}>
  {#snippet head()}
    <th>Scope</th>
    <th>Access</th>
  {/snippet}
  {#snippet row(scope)}
    <td><code>{scope.name}</code></td>
    <td>{scope.description}</td>
  {/snippet}
</SpecBlock>
