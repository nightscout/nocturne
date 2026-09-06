//! Parsing and validation for `nocturne-connect://link?server=…&token=…` link codes.
//!
//! The same untrusted string arrives two ways — pasted by the user, or handed over by the OS when
//! a `nocturne-connect://` link is opened in a browser — so both paths land here.

use url::Url;

pub const SCHEME: &str = "nocturne-connect";

const NOT_A_LINK_CODE: &str =
    "That doesn't look like a link code. Copy the whole nocturne-connect:// line from Nocturne.";
const MISSING_PARTS: &str =
    "The link code is missing its server or token part. Generate a fresh one in Nocturne.";
const BAD_SERVER: &str = "The link code's server address is not a valid URL.";

/// A validated link code: an http(s) server origin with no trailing slash, plus the bearer it
/// minted.
#[derive(Debug, PartialEq)]
pub struct LinkCredentials {
    pub server_url: String,
    pub token: String,
}

pub fn parse(raw: &str) -> Result<LinkCredentials, &'static str> {
    let parsed = Url::parse(raw.trim()).map_err(|_| NOT_A_LINK_CODE)?;

    if parsed.scheme() != SCHEME {
        return Err(NOT_A_LINK_CODE);
    }

    let mut server = None;
    let mut token = None;
    for (key, value) in parsed.query_pairs() {
        match key.as_ref() {
            "server" => server = Some(value.to_string()),
            "token" => token = Some(value.to_string()),
            _ => {}
        }
    }

    let (server, token) = match (server, token) {
        (Some(s), Some(t)) if !s.is_empty() && !t.is_empty() => (s, t),
        _ => return Err(MISSING_PARTS),
    };

    let server_url = Url::parse(&server)
        .ok()
        .filter(|u| matches!(u.scheme(), "http" | "https"))
        .ok_or(BAD_SERVER)?;

    Ok(LinkCredentials {
        server_url: server_url.as_str().trim_end_matches('/').to_string(),
        token,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn code(server: &str) -> String {
        format!("nocturne-connect://link?server={server}&token=abc123")
    }

    #[test]
    fn accepts_a_well_formed_code() {
        let parsed = parse(&code("https%3A%2F%2Fdemo.nocturne.run")).unwrap();
        assert_eq!(parsed.server_url, "https://demo.nocturne.run");
        assert_eq!(parsed.token, "abc123");
    }

    #[test]
    fn trims_surrounding_whitespace() {
        let raw = format!("  {}\n", code("https%3A%2F%2Fdemo.nocturne.run"));
        assert_eq!(parse(&raw).unwrap().server_url, "https://demo.nocturne.run");
    }

    #[test]
    fn strips_the_trailing_slash_from_the_server() {
        let parsed = parse(&code("https%3A%2F%2Fdemo.nocturne.run%2F")).unwrap();
        assert_eq!(parsed.server_url, "https://demo.nocturne.run");
    }

    #[test]
    fn rejects_another_scheme() {
        assert_eq!(
            parse("https://demo.nocturne.run/link?server=https://x&token=abc"),
            Err(NOT_A_LINK_CODE)
        );
    }

    #[test]
    fn rejects_a_non_url() {
        assert_eq!(parse("abc123"), Err(NOT_A_LINK_CODE));
    }

    #[test]
    fn rejects_a_missing_or_empty_part() {
        assert_eq!(
            parse("nocturne-connect://link?server=https%3A%2F%2Fdemo.nocturne.run"),
            Err(MISSING_PARTS)
        );
        assert_eq!(parse("nocturne-connect://link?token=abc123"), Err(MISSING_PARTS));
        assert_eq!(
            parse("nocturne-connect://link?server=https%3A%2F%2Fdemo.nocturne.run&token="),
            Err(MISSING_PARTS)
        );
    }

    #[test]
    fn rejects_a_non_http_server_scheme() {
        assert_eq!(parse(&code("file%3A%2F%2F%2FC%3A%2Fwindows")), Err(BAD_SERVER));
        assert_eq!(parse(&code("javascript%3Aalert(1)")), Err(BAD_SERVER));
        assert_eq!(parse(&code("data%3Atext%2Fhtml%2Chi")), Err(BAD_SERVER));
    }

    #[test]
    fn rejects_a_hostless_server() {
        assert_eq!(parse(&code("http%3A%2F%2F")), Err(BAD_SERVER));
    }

    #[test]
    fn last_duplicated_parameter_wins() {
        let parsed = parse(
            "nocturne-connect://link?server=https%3A%2F%2Fa.example&token=one&token=two",
        )
        .unwrap();
        assert_eq!(parsed.token, "two");
        assert_eq!(parsed.server_url, "https://a.example");
    }
}
