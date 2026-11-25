// A smooth scroll has no bounded duration, so a test waits on `scrollend` and not on a time limit.
// An earlier instant scroll sends its `scrollend` after the call returns, so an end at the starting position is not this action's.
export async function afterScroll(action: () => Promise<unknown>, target: Window | Element = window): Promise<void> {
  const position = () => (target instanceof Window ? `${target.scrollX},${target.scrollY}` : `${target.scrollLeft},${target.scrollTop}`);
  const from = position();
  const ended = new Promise<void>((resolve) => {
    const onEnd = () => {
      if (position() === from) return;
      target.removeEventListener("scrollend", onEnd);
      resolve();
    };
    target.addEventListener("scrollend", onEnd);
  });
  await action();
  await ended;
}
