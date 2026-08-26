import { findBrokenReferences } from './references.js';
import { report } from './report.js';

report(
	'Broken screenshot references',
	await findBrokenReferences(),
	'All screenshot references resolve.',
);
