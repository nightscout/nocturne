import { mount } from 'svelte';
import { getEngineHost } from '../src';
import Scenario from './Scenario.svelte';
import { begin } from './hooks';

const query = new URLSearchParams(location.search);
const scenario = query.get('s') ?? 'hero';
const n = Number(query.get('n')) || undefined;

(window as unknown as { __benchEngineStats: () => unknown }).__benchEngineStats = () => getEngineHost().stats();

async function start() {
  if (query.get('warm') === '1') await getEngineHost().warm();
  begin();
  mount(Scenario, { target: document.body, props: { scenario, n } });
}
void start();
