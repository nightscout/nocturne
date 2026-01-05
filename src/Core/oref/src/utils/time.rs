//! Time and timestamp utilities

use chrono::{DateTime, Utc};
use crate::Result;
use crate::OrefError;

/// Parse a timestamp string into a DateTime
///
/// Supports multiple formats:
/// - RFC3339: "2024-01-01T12:00:00Z"
/// - ISO with space: "2024-01-01 12:00:00"
/// - Unix milliseconds: "1704110400000"
pub fn parse_timestamp(s: &str) -> Result<DateTime<Utc>> {
    // Try RFC3339 first
    if let Ok(dt) = DateTime::parse_from_rfc3339(s) {
        return Ok(dt.with_timezone(&Utc));
    }

    // Try common ISO format with space
    if let Ok(dt) = DateTime::parse_from_str(s, "%Y-%m-%d %H:%M:%S") {
        return Ok(dt.with_timezone(&Utc));
    }

    // Try Unix milliseconds
    if let Ok(millis) = s.parse::<i64>() {
        if let Some(dt) = DateTime::from_timestamp_millis(millis) {
            return Ok(dt);
        }
    }

    Err(OrefError::InvalidTimestamp(s.to_string()))
}

/// Format a DateTime as RFC3339
pub fn format_timestamp(dt: DateTime<Utc>) -> String {
    dt.to_rfc3339()
}

#[cfg(test)]
mod tests {
    use super::*;
    use chrono::{Datelike, Timelike};

    #[test]
    fn test_parse_rfc3339() {
        let result = parse_timestamp("2024-01-01T12:00:00Z").unwrap();
        assert_eq!(result.year(), 2024);
        assert_eq!(result.month(), 1);
        assert_eq!(result.day(), 1);
        assert_eq!(result.hour(), 12);
    }

    #[test]
    fn test_parse_millis() {
        let result = parse_timestamp("1704110400000").unwrap();
        assert_eq!(result.year(), 2024);
    }

    #[test]
    fn test_round_trip() {
        let original = Utc::now();
        let formatted = format_timestamp(original);
        let parsed = parse_timestamp(&formatted).unwrap();

        // Should be within 1 second (nanoseconds may differ)
        assert!((original - parsed).num_seconds().abs() < 1);
    }

    #[test]
    fn test_millis_round_trip() {
        let original = Utc::now();
        let millis = original.timestamp_millis();
        let restored = DateTime::from_timestamp_millis(millis).unwrap();

        // Should be within 1 millisecond
        assert!((original - restored).num_milliseconds().abs() < 1);
    }
}
