import { getContext, hasContext, setContext } from 'svelte';

const KEY = Symbol.for('nocturne.watercolour.drop-group');

/**
 * What a run of surfaces shares.
 *
 * Members claim their own index in mount order, so a parent lays out a grid or
 * a list without threading indices by hand. The only other thing they share is
 * the seed; variety comes from it rather than from what a neighbour drew.
 */
export interface DropGroupContext {
  /** Claimed once per member, at init. */
  claim(): number;
  readonly seed: number;
  release(index: number): void;
}

export function createDropGroup(seed: number): DropGroupContext {
  let next = 0;
  return {
    claim: () => next++,
    get seed() {
      return seed;
    },
    release: () => {},
  };
}

export function setDropGroup(group: DropGroupContext): void {
  setContext(KEY, group);
}

/** The enclosing group, or `undefined` for a surface standing on its own. */
export function getDropGroup(): DropGroupContext | undefined {
  return hasContext(KEY) ? getContext<DropGroupContext>(KEY) : undefined;
}
