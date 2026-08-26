import { findBrokenEmbeds } from './embeds.js';
import { report } from './report.js';

report(
	'Broken screenshot embeds',
	await findBrokenEmbeds(),
	'All documentation screenshot embeds resolve.',
);
