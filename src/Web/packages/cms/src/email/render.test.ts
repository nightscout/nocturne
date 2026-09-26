import { describe, it, expect, vi } from 'vitest';
import { handleEmailRenderRequest, isAuthorised, type EmailRenderOptions } from './render.ts';

const SECRET = 'correct horse battery staple';

function options(secret = SECRET): EmailRenderOptions {
	return {
		componentMap: {},
		secret,
		renderTemplate: vi.fn(async (template: string, locale: string) => `<p>${template}:${locale}</p>`),
	};
}

function post(body: unknown, header?: string): Request {
	const headers = new Headers({ 'Content-Type': 'application/json' });
	if (header !== undefined) headers.set('x-email-render-secret', header);
	return new Request('http://localhost/email/render', {
		method: 'POST',
		headers,
		body: typeof body === 'string' ? body : JSON.stringify(body),
	});
}

const validBody = { template: 'weekly-summary', locale: 'en', data: { userName: 'Rhys' } };

describe('isAuthorised', () => {
	it('accepts the configured secret', () => {
		expect(isAuthorised(SECRET, SECRET)).toBe(true);
	});

	it('rejects a different secret of the same length', () => {
		expect(isAuthorised(SECRET.replace('c', 'k'), SECRET)).toBe(false);
	});

	it('rejects a prefix or extension of the secret', () => {
		expect(isAuthorised(SECRET.slice(0, 5), SECRET)).toBe(false);
		expect(isAuthorised(`${SECRET}x`, SECRET)).toBe(false);
	});

	it('rejects a missing header', () => {
		expect(isAuthorised(null, SECRET)).toBe(false);
	});

	it('rejects everything when no secret is configured', () => {
		expect(isAuthorised('', '')).toBe(false);
		expect(isAuthorised('anything', '')).toBe(false);
	});
});

describe('handleEmailRenderRequest', () => {
	it('renders with the right secret', async () => {
		const opts = options();
		const res = await handleEmailRenderRequest(post(validBody, SECRET), opts);
		expect(res.status).toBe(200);
		expect(await res.text()).toBe('<p>weekly-summary:en</p>');
		expect(opts.renderTemplate).toHaveBeenCalledWith('weekly-summary', 'en', { userName: 'Rhys' });
	});

	it('returns 401 without the header, and does not render', async () => {
		const opts = options();
		const res = await handleEmailRenderRequest(post(validBody), opts);
		expect(res.status).toBe(401);
		expect(opts.renderTemplate).not.toHaveBeenCalled();
	});

	it('returns 401 for a wrong secret', async () => {
		const res = await handleEmailRenderRequest(post(validBody, 'wrong'), options());
		expect(res.status).toBe(401);
	});

	it('returns 401 when the secret is unset, even for an empty header', async () => {
		const opts = options('');
		const res = await handleEmailRenderRequest(post(validBody, ''), opts);
		expect(res.status).toBe(401);
		expect(opts.renderTemplate).not.toHaveBeenCalled();
	});

	it('defaults missing data to an empty object', async () => {
		const opts = options();
		const res = await handleEmailRenderRequest(post({ template: 't', locale: 'en' }, SECRET), opts);
		expect(res.status).toBe(200);
		expect(opts.renderTemplate).toHaveBeenCalledWith('t', 'en', {});
	});

	it('returns 400 for missing fields', async () => {
		const res = await handleEmailRenderRequest(post({ template: 't' }, SECRET), options());
		expect(res.status).toBe(400);
	});

	it('returns 400 for a body that is not JSON', async () => {
		const res = await handleEmailRenderRequest(post('not json', SECRET), options());
		expect(res.status).toBe(400);
	});

	it('returns 400 for non-string data values', async () => {
		const res = await handleEmailRenderRequest(
			post({ ...validBody, data: { userName: { toString: 1 } } }, SECRET),
			options(),
		);
		expect(res.status).toBe(400);
	});
});
