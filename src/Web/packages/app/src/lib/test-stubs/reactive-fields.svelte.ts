/**
 * An object whose fields a test can reassign after a component has rendered from it, which
 * re-renders that component as the real store does. Plain objects stand in for stores whose
 * values only matter at first render.
 */
export function reactiveFields<T extends Record<string, unknown>>(initial: T): T {
  const fields = $state({ ...initial });
  const target = {} as T;
  for (const key of Object.keys(initial) as (keyof T)[]) {
    Object.defineProperty(target, key, {
      get: () => fields[key],
      set: (value: T[keyof T]) => {
        fields[key] = value;
      },
      enumerable: true,
    });
  }
  return target;
}
