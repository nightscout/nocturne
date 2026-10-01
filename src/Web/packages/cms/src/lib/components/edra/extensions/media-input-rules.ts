// `![alt](src "title")` typed just before the cursor, where src names a file of that kind, so
// the audio and video rules never claim each other's links. Group 1 is the whole token, which
// nodeInputRule replaces; group 3 is src. The alt and title classes exclude their own delimiters
// and the match is anchored at the cursor, so a long text node cannot backtrack.

export const AUDIO_INPUT_REGEX =
	// eslint-disable-next-line security/detect-unsafe-regex -- optional groups hold the only nested quantifiers; anchored at the cursor it stays linear (media-input-rules.test.ts times it)
	/(?:^|\s)(!\[([^\]]*)\]\((\S+\.(?:mp3|wav|ogg|oga|m4a|aac|flac|opus|weba)(?:[?#][^\s)]*)?)(?:\s+["']([^"']*)["'])?\))$/i;

export const VIDEO_INPUT_REGEX =
	// eslint-disable-next-line security/detect-unsafe-regex -- optional groups hold the only nested quantifiers; anchored at the cursor it stays linear (media-input-rules.test.ts times it)
	/(?:^|\s)(!\[([^\]]*)\]\((\S+\.(?:mp4|webm|ogv|mov|m4v|mkv)(?:[?#][^\s)]*)?)(?:\s+["']([^"']*)["'])?\))$/i;
