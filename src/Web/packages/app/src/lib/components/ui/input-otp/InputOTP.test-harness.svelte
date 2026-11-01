<script lang="ts">
  // Test-only wrapper: a complete code runs a callback that reads and writes the same state, as a
  // remote form's submit does with its pending counter.
  import * as InputOTP from "#lib/components/ui/input-otp/index.js";

  let { onCompleteCalled }: { onCompleteCalled?: (count: number) => void } = $props();

  let value = $state("");
  let completions = $state(0);
</script>

<InputOTP.Root
  maxlength={4}
  bind:value
  inputId="code"
  onComplete={() => {
    completions++;
    onCompleteCalled?.(completions);
  }}
>
  {#snippet children({ cells })}
    <InputOTP.Group>
      {#each cells as cell, i (i)}
        <InputOTP.Slot {cell} />
      {/each}
    </InputOTP.Group>
  {/snippet}
</InputOTP.Root>

<p data-testid="completions">{completions}</p>
