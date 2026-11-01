<script lang="ts">
  import { untrack } from "svelte";
  import { PinInput as InputOTPPrimitive } from "bits-ui";
  import { cn } from "../../../utils";

  let {
    ref = $bindable(null),
    class: className,
    value = $bindable(""),
    onComplete,
    ...restProps
  }: InputOTPPrimitive.RootProps = $props();

  /**
   * bits-ui calls `onComplete` from inside the `$effect` that watches the value, so every signal
   * the callback reads joins that effect's dependencies. A callback that reads and writes the same
   * state, such as a remote form's `pending` counter when the callback submits the form, re-runs
   * the effect, which calls the callback again: `effect_update_depth_exceeded`. Untracking the
   * callback keeps its reads out of the effect.
   */
  function complete(code: string) {
    untrack(() => onComplete?.(code));
  }
</script>

<InputOTPPrimitive.Root
  bind:ref
  bind:value
  onComplete={complete}
  data-slot="input-otp"
  class={cn(
    "has-disabled:opacity-50 flex items-center gap-2 [&_input]:disabled:cursor-not-allowed",
    className
  )}
  {...restProps}
/>
