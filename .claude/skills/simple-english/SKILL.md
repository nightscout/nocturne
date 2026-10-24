---
name: simple-english
description: |
  Write or rewrite the text Nocturne shows its users with the rules of
  ASD-STE100 Simplified Technical English, so the interface reads as one voice
  and a tired reader cannot misread it. Use when adding or changing any
  user-facing string in src/Web (app or portal docs): labels, buttons, empty
  states, errors, toasts, help text, dialogs, and docs pages. Also use when the
  user says "STE", "Simplified Technical English", "ASD-STE100", "UI copy", or
  "de-slop". Covers the judgment half of the standard. Vale enforces the
  mechanical half through `pnpm run lint:copy`.
metadata:
  standard: ASD-STE100 Issue 9 (2025-01-15)
---

# Simple English for Nocturne UI copy

Nocturne's reader is often tired, worried, or reading at 3 a.m. during a low.
Many readers are not native English speakers, and every string is machine
translated into other languages. Each string must survive one read, and it must
mean the same thing on every page.

STE is the controlled language that aerospace manufacturers use for maintenance
documentation. Its rules give short sentences, one word for one meaning, and no
filler. That is what a unified interface needs.

## Vale owns the mechanical rules. Run it.

```
cd src/Web
pnpm run lint:copy                        # every alert, grouped by rule
pnpm run lint:copy --min=error            # what fails CI
pnpm run lint:copy --rule=Nocturne.Terms  # one rule
```

The script extracts every string from the source (wuchale writes them to
`locales/en.po`), runs Vale over them, and prints each alert with the
components that use the string. Vale catches:

> semicolons · emdashes · `should` and the other unapproved modals · British
> spelling · contractions · `e.g.`/`i.e.`/`etc.` · slop and filler words ·
> perfect tenses · sentences over 25 words · phrasal verbs · trailing
> conditions · exclamation marks · apologies and mood words · rotated terms
> ("log in" beside "sign in") · "click" and "tap" · banned phrases ("asks for")

Rules live in `.vale/styles/Nocturne/`. Errors fail CI. Warnings and
suggestions are advisory.

**When a rule is wrong, fix the rule, not the sentence.** If one string must
break a rule on purpose, add a `rule<TAB>msgid` line to
`src/Web/utils/lintUiCopy/exceptions.txt` with a comment that gives the reason.

Everything below is what Vale cannot decide.

## Scope

Lint covers the app (`packages/app`), the CMS, and the portal docs
(`packages/portal`, paths with `/docs/`). The portal's marketing pages (home,
features, blog, roadmap) are out of scope: STE deletes persuasion by design.

## Spelling and terms

- American English: color, behavior, canceled, analyze, organize.
- One term per concept. The current choices:

| Use | Not |
|---|---|
| sign in, sign out, sign-in page | log in, log out, login |
| glucose | blood sugar |
| carbs | carbohydrates |
| could not | unable to |
| select | click, tap |
| email | e-mail |

"Blood glucose" and "BG" stay: a meter reading is a different measurement from
a sensor reading, and those words mark the difference. When you add a new term
pair, add it to `Terms.yml` with the form the UI already uses most.

## Is this an instruction or a description?

| | Instruction | Description |
|---|---|---|
| Purpose | Tell the reader what to do | Say what a thing is or does |
| Verb form | Imperative: "Select a sensor." | Simple present or past |
| Sentence limit | **20 words** (Rule 5.1) | **25 words** (Rule 6.3) |

Vale enforces only the 25-word limit. In an instruction, hold yourself to 20.

## String types

- **Labels, buttons, headings, menu items**: technical names, exempt from the
  sentence rules. Use the same word for the same action everywhere. A button
  that deletes says "Delete", not "Remove" on one page and "Delete" on another.
- **Empty states**: say what is absent, then the next step. "No sensors yet.
  Add a sensor to start." Never cheerful.
- **Errors and toasts**: what happened (simple past), the cause if known, then
  the fix as an imperative. "Could not save the profile. The server did not
  respond. Try again." No "Oops", no "Sorry", no "Something went wrong".
- **Confirmations**: say what the action will do, then the risk.
- **Help text and docs**: descriptive unless it is a procedure. One fact per
  sentence. Use a list when a sentence gets long.

## Judgment rules

- **Condition before command** (5.4): "If the sensor is new, wait two hours."
  Vale catches only the comma-trailing form.
- **One instruction per sentence** (5.2), and one new fact per sentence (6.1).
- **Active voice** (3.6). The reader or Nocturne is the actor, never a screen:
  "Enter the code", not "the page asks for the code".
- **Complete grammar** (4.2): keep articles and "that". STE is short sentences,
  not telegraph style.
- **A verb for an action** (3.7): "compress the file", not "perform compression".
- **Noun clusters of three words or fewer** (2.1): "the timeout value for the
  connection" over "the connection timeout configuration value".
- **Approved modals are can, will, and must** (3.2). "Should" reads as optional.

## Safety copy

Glucose, insulin, and alarm text can cause harm if misread. Order it:

1. The risk word. WARNING means injury. CAUTION means damage or data loss.
2. The command or condition.
3. Then the result.

**Before:** Note that alarms may not sound in some cases if the phone's battery
saver happens to be on.
**After:** WARNING: Turn off battery saver for Nocturne. Battery saver can stop
alarms.

## Untouchables

Leave these exact, even when they break a rule:

- Placeholders (`{0}`) and wuchale element tags (`<0>`, `<0/>`)
- Product and company names (Dexcom, xDrip+, LibreLinkUp, AAPS, Nightscout)
- Units (mg/dL, mmol/L, U/hr), identifiers, commands, file paths, API scopes
- Quoted text from a device or a third party

## Changing a string

A changed string is a new msgid. wuchale drops the old translations and the
sync job translates the new one. Change the source literal in the component,
never `.po` files by hand.

## Self-check

1. `pnpm run lint:copy --min=error` is clean.
2. Instructions are imperative and inside 20 words.
3. Every condition stands before its command.
4. One term per concept, from the table above.
5. Safety copy: risk word, command, result.
6. Untouchables intact.

No tool can guarantee ASD-STE100 compliance. This skill is an unofficial aid,
not endorsed by ASD. The official standard is free at asd-ste100.org.
