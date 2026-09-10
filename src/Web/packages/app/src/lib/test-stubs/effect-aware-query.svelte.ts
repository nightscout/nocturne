function isInEffect(): boolean {
  try {
    $effect.pre(() => {});
    return true;
  } catch {
    return false;
  }
}

export function effectAwareQuery<T>(read: () => Promise<T>) {
  return {
    run(): Promise<T> {
      // SvelteKit 2.59.1 detects effects this way; $effect.tracking() misses onMount.
      if (isInEffect()) {
        throw new Error(
          "On the client, .run() can only be called outside render, e.g. in universal `load` functions and event handlers. In render, await the query directly",
        );
      }
      return read();
    },
  };
}
