/**
 * Performs a film's `do` actions in a live app, in film order, as `film()` performs them in a
 * render, so a capture records what the app answers after each one. `onAction(k)` is called before
 * the kth action (from 1), so a capture can tag the requests that action causes.
 */
export async function performFilmActions(page, film, onAction = () => {}) {
  // The page never navigates, so the load state says nothing: an action is done once no remote
  // function call has been in flight for a while.
  const pending = new Set();
  let quietSince = Date.now();
  const remote = (req) => new URL(req.url()).pathname.startsWith("/_app/remote/");
  const start = (req) => remote(req) && pending.add(req);
  const end = (req) => {
    if (pending.delete(req) && pending.size === 0) quietSince = Date.now();
  };
  page.on("request", start);
  page.on("requestfinished", end);
  page.on("requestfailed", end);
  const quiet = async () => {
    const deadline = Date.now() + 120_000;
    while (Date.now() < deadline && (pending.size > 0 || Date.now() - quietSince < 800)) await page.waitForTimeout(100);
  };
  let k = 0;
  for (const scene of film?.scenes ?? []) {
    for (const beat of scene.beats) {
      for (const spec of beat.do ?? []) {
        k++;
        onAction(k);
        const selector = spec.click ?? spec.type ?? spec.choose;
        let target = page.locator(`${selector} >> visible=true`);
        if (spec.click !== undefined && spec.text !== undefined) target = target.filter({ hasText: spec.text });
        target = target.first();
        if (spec.type !== undefined) await target.fill(spec.text);
        else if (spec.choose !== undefined) await target.selectOption(spec.value);
        else await target.click();
        await page.waitForTimeout(200);
        await quiet();
      }
    }
  }
  page.off("request", start);
  page.off("requestfinished", end);
  page.off("requestfailed", end);
  return k;
}
