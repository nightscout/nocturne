//! Three-valued truth (engine-semantics.md §2.4): a node is true, false, or
//! unknown when a fact it compares is one the host has not got.

use serde_json::{Map, Value};

/// Kleene's strong three-valued logic.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum Truth {
    False,
    True,
    Unknown,
}

impl Truth {
    /// False when any is false, else unknown when any is unknown, else true;
    /// true for none.
    #[must_use]
    pub fn all(values: impl IntoIterator<Item = Truth>) -> Truth {
        values
            .into_iter()
            .fold(Truth::True, |acc, v| match (acc, v) {
                (Truth::False, _) | (_, Truth::False) => Truth::False,
                (Truth::Unknown, _) | (_, Truth::Unknown) => Truth::Unknown,
                (Truth::True, Truth::True) => Truth::True,
            })
    }

    /// True when any is true, else unknown when any is unknown, else false;
    /// false for none.
    #[must_use]
    pub fn any(values: impl IntoIterator<Item = Truth>) -> Truth {
        !Truth::all(values.into_iter().map(|v| !v))
    }

    /// `None` is unknown.
    #[must_use]
    pub fn of(value: Option<bool>) -> Truth {
        value.map_or(Truth::Unknown, Truth::from)
    }

    #[must_use]
    pub fn is_true(self) -> bool {
        self == Truth::True
    }

    #[must_use]
    pub fn is_false(self) -> bool {
        self == Truth::False
    }

    #[must_use]
    pub fn is_unknown(self) -> bool {
        self == Truth::Unknown
    }

    /// The known value, `None` when unknown.
    #[must_use]
    pub fn known(self) -> Option<bool> {
        match self {
            Truth::True => Some(true),
            Truth::False => Some(false),
            Truth::Unknown => None,
        }
    }

    /// Writes the wire form into `o`: `field` is a boolean that reads false
    /// when unknown, with `"unknown": true` beside it only then, so a reader
    /// that ignores the flag sees a two-valued result.
    pub fn write(self, o: &mut Map<String, Value>, field: &str) {
        o.insert(field.into(), self.is_true().into());
        if self.is_unknown() {
            o.insert("unknown".into(), true.into());
        }
    }
}

impl From<bool> for Truth {
    fn from(value: bool) -> Self {
        if value { Truth::True } else { Truth::False }
    }
}

impl std::ops::Not for Truth {
    type Output = Truth;

    fn not(self) -> Truth {
        match self {
            Truth::True => Truth::False,
            Truth::False => Truth::True,
            Truth::Unknown => Truth::Unknown,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::Truth::{self, False, True, Unknown};

    const ALL: [Truth; 3] = [False, True, Unknown];

    #[test]
    fn and_is_false_on_any_false_then_unknown_on_any_unknown() {
        for a in ALL {
            for b in ALL {
                let expected = if a == False || b == False {
                    False
                } else if a == Unknown || b == Unknown {
                    Unknown
                } else {
                    True
                };
                assert_eq!(Truth::all([a, b]), expected, "{a:?} and {b:?}");
            }
        }
    }

    #[test]
    fn or_is_true_on_any_true_then_unknown_on_any_unknown() {
        for a in ALL {
            for b in ALL {
                let expected = if a == True || b == True {
                    True
                } else if a == Unknown || b == Unknown {
                    Unknown
                } else {
                    False
                };
                assert_eq!(Truth::any([a, b]), expected, "{a:?} or {b:?}");
            }
        }
    }

    #[test]
    fn not_keeps_unknown() {
        assert_eq!(!True, False);
        assert_eq!(!False, True);
        assert_eq!(!Unknown, Unknown);
    }
}
