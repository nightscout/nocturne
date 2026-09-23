import { describe, it, expect } from 'vitest';
import { AUDIO_INPUT_REGEX, VIDEO_INPUT_REGEX } from './media-input-rules.ts';

describe('media input rules', () => {
	it('match a link typed just before the cursor, with the whole token in group 1 and src in group 3', () => {
		const audio = 'Listen: ![theme](https://example.com/theme.mp3)'.match(AUDIO_INPUT_REGEX);
		expect(audio?.[1]).toBe('![theme](https://example.com/theme.mp3)');
		expect(audio?.[3]).toBe('https://example.com/theme.mp3');

		const video = '![clip](/media/clip.webm "A clip")'.match(VIDEO_INPUT_REGEX);
		expect(video?.[1]).toBe('![clip](/media/clip.webm "A clip")');
		expect(video?.[3]).toBe('/media/clip.webm');
		expect(video?.[4]).toBe('A clip');
	});

	it('keeps a query string or fragment on src', () => {
		expect('![a](song.ogg?t=30)'.match(AUDIO_INPUT_REGEX)?.[3]).toBe('song.ogg?t=30');
		expect('![v](movie.MP4#t=5)'.match(VIDEO_INPUT_REGEX)?.[3]).toBe('movie.MP4#t=5');
	});

	it('are distinct: each claims only its own kind of file', () => {
		expect(AUDIO_INPUT_REGEX.test('![a](clip.mp4)')).toBe(false);
		expect(VIDEO_INPUT_REGEX.test('![a](song.mp3)')).toBe(false);
		for (const regex of [AUDIO_INPUT_REGEX, VIDEO_INPUT_REGEX]) {
			expect(regex.test('![a](photo.png)')).toBe(false);
		}
	});

	it('ignore a link earlier in the paragraph once more text follows it', () => {
		expect(AUDIO_INPUT_REGEX.test('![a](song.mp3) and then more text')).toBe(false);
		expect(VIDEO_INPUT_REGEX.test('![v](clip.mp4) and then more text)')).toBe(false);
	});

	it('need the token to start the text or follow whitespace', () => {
		expect(AUDIO_INPUT_REGEX.test('word![a](song.mp3)')).toBe(false);
	});

	it('stay fast over a long text node full of earlier links', () => {
		const text = '![a](b.mp3) '.repeat(700) + '![' + 'x'.repeat(500) + '](y';
		const start = performance.now();
		for (const regex of [AUDIO_INPUT_REGEX, VIDEO_INPUT_REGEX]) {
			for (let i = 0; i < 20; i++) regex.test(text);
		}
		expect(performance.now() - start).toBeLessThan(250);
	});
});
