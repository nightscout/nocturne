---
displayName: Sharing and history limits
standalone: true
---
The ways a person gives someone else access to their data, and what a client sees when access is limited.

## Ways to give access

| Method | For | Access |
| --- | --- | --- |
| Member invite | A caregiver or a clinician who needs lasting access. | A role in the tenant. Can include writes. |
| Guest link | Short-term access, such as a school day or a clinic visit. | Read-only, for 48 hours. |
| Public share link | Anyone who has the link. | Read-only, with no sign-in. |
| Connected app | A third-party app. | The scopes that the person approved through OAuth. |

No method can give more access than the person who gives it has.

## Public share link

A share link is a host name: `https://<token>.share.<base-domain>`. A request to that host needs no credential, and any credential it carries is ignored.

- The owner selects which categories the link shows. A new link shows glucose only.
- The link is read-only.
- The link shows the last 24 hours, unless the owner turns on full history.
- `POST /api/v4/share/rotate` replaces the token. The old link stops working.

## Guest link

A guest link is a short code, valid for 48 hours. The guest enters the code in the web app, which calls `POST /api/v4/guest-links/activate` and receives a session cookie. A tenant can have five active guest links.

## The 24-hour limit

A member, an API token, an OAuth grant, a guest link and a share link can each be limited to the last 24 hours of data.

For a limited reader:

- A read returns only the records of the last 24 hours. Older records are absent, not refused: a list is shorter, and an older record is not found by its id.
- A state that started earlier and is still in effect, such as the active profile or a pump mode, is still returned.
- Statistics and reports cover only what the reader can see.
- Writes are not limited.

The limit is applied in the database, so it is the same for every read, including the legacy Nightscout API.

A limited reader cannot create access that is wider than its own. An API token, an invite or a guest link that it creates has the same limit.

## Check what a credential can do

`GET /api/v4/me/permissions` returns the permissions of the current request in this tenant.
