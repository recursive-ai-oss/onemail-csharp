# Provider setup and smoke runs

How to point `RecursiveAI.OneMail.Client` at Gmail and Outlook.com with OAuth 2.0, and how the provider smoke runner (`tools/providers`) checks it. The library never acquires tokens: it takes an access token in `SaslCredentials.AccessToken` and speaks `XOAUTH2` or `OAUTHBEARER` (`sasl.md`). Getting and refreshing the token is the caller's job.

## Endpoints

| Provider | IMAP | SMTP submission | POP3 | SASL for OAuth |
|---|---|---|---|---|
| Gmail | `imap.gmail.com:993` (implicit TLS) | `smtp.gmail.com:587` (STARTTLS), `:465` (implicit TLS) | `pop.gmail.com:995` (must be enabled in the account) | `XOAUTH2`, `OAUTHBEARER` |
| Outlook.com (personal) | `outlook.office365.com:993` | `smtp-mail.outlook.com:587` (STARTTLS) | `outlook.office365.com:995` | `XOAUTH2` (Microsoft documents only this one) |

The user name for SASL is the full address.

## Your own OAuth app

**Google.** In a Google Cloud project: enable no API (IMAP/SMTP need none), configure the OAuth consent screen (External; add the mailbox as a test user while the app is in Testing), then create an OAuth client of type *Desktop app*. Scope: `https://mail.google.com/` (the only scope Google accepts for IMAP, POP and SMTP). Flow: installed-app loopback redirect (`http://127.0.0.1:<port>/`) with PKCE, `access_type=offline` and `prompt=consent` to receive a refresh token. A Testing-status app's refresh tokens expire after 7 days.

**Microsoft.** In Entra (App registrations): new registration, *Personal Microsoft accounts only* (or "any tenant and personal"), enable *Allow public client flows*, add the delegated permissions `IMAP.AccessAsUser.All`, `POP.AccessAsUser.All` and `SMTP.Send` (Office 365 Exchange Online / `outlook.office.com`) and `offline_access`. Flow: device code against `https://login.microsoftonline.com/consumers/oauth2/v2.0/`. Microsoft rotates refresh tokens: store the one each refresh returns.

## The smoke runner

```
dotnet run --project tools/providers -- token gmail                 # prints the consent URL, saves the PKCE state
dotnet run --project tools/providers -- token gmail --complete <redirected-URL|code|@file|->   # state checked; stores the refresh token
dotnet run --project tools/providers -- token outlook               # device code
dotnet run --project tools/providers -- smoke gmail|outlook [--protocols imap,smtp,pop] [--trace] [--out rows.md]
```

Everything comes from environment variables or the env file named by `ONEMAIL_PROVIDERS_ENV` (the process environment wins):

| Variable | Provider | Meaning |
|---|---|---|
| `GMAIL_ADDRESS`, `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET` | Gmail | the mailbox, the Desktop client (the secret is what Google's token endpoint expects) |
| `MS_ADDRESS`, `MS_CLIENT_ID`, `MS_TENANT` | Outlook.com | the mailbox, the public client, the tenant segment (default `consumers`) |
| `GOOGLE_REFRESH_TOKEN`, `MS_REFRESH_TOKEN` | both | written into the env file by `token` (or into the file `ONEMAIL_PROVIDERS_TOKENS` names); keep it mode 600 |

Without the credentials `smoke` prints "skipped" and exits 77; nothing else runs. It sends only to the account itself (tens of messages: one per auth mechanism and security mode, plus an attachment message, an SMTPUTF8 message where offered and a 5 MB message), tags every message `X-OneMail-Smoke: onemail-smoke-<run>`, and always ends with a cleanup that finds the tag in every selectable folder, moves it to Trash and deletes it there. Re-running is safe, and a run also removes leftovers from earlier aborted runs. Rows are `pass`, `FAIL` or `not-offered` (the server did not advertise the capability); the wire trace (`--trace`, stderr) masks tokens, long base64 words and the address.

What each row checks: SMTP auth+send per mechanism and port; IMAP login per mechanism, LIST with SPECIAL-USE, SELECT, FETCH `BODY[]` compared with the bytes sent (body bytes equal, every sent header intact; added trace headers counted), SEARCH, STORE flags, CONDSTORE, QRESYNC, APPEND, MOVE, IDLE (a message sent over SMTP must raise EXISTS within 90 s), COMPRESS=DEFLATE; POP3 XOAUTH2, UIDL, RETR.

## Consent from another machine

`token gmail` does not listen on a port, so the approving browser can be anywhere. The redirect target is `http://127.0.0.1:53682/` (Google accepts any loopback port for Desktop clients). After approval that page does not load; copy the whole URL from the address bar into `--complete`. A bare `code` also works. A full URL must carry the saved `state`. Codes live a few minutes. While the consent screen is in Testing status the refresh token expires after 7 days; mint again.

## Quirks

Gmail (observed 2026-11-04):

- SMTP offers `SIZE 8BITMIME ENHANCEDSTATUSCODES PIPELINING CHUNKING SMTPUTF8 LIMITS`; 587 STARTTLS and 465 implicit both take XOAUTH2 and OAUTHBEARER. Our BDAT path, a raw UTF-8 subject under SMTPUTF8 and a 5.3 MB message all deliver.
- IMAP offers `IDLE MOVE CONDSTORE ESEARCH UTF8=ACCEPT LIST-EXTENDED LIST-STATUS LITERAL- SPECIAL-USE COMPRESS=DEFLATE UIDPLUS APPENDLIMIT=35651584 X-GM-EXT-1`; no QRESYNC, no LITERAL+. IDLE pushes EXISTS for a message sent over SMTP within seconds.
- `SEARCH HEADER Message-ID <text>` matches whole words, not substrings: search for the complete id.
- Gmail's folders are labels: moving to Trash and deleting there removes a message. `[Gmail]/All Mail`, Starred and Important show the same messages and are skipped by cleanup.
- Gmail adds two headers on the way in; every other header and the body come back byte-for-byte. `RFC822.SIZE` is larger than what we sent by those headers.
- POP3 is off by default: `-ERR Your account is not enabled for POP access`. Turn it on in Gmail settings (Forwarding and POP/IMAP); the runner reports `not-offered` until then. With POP on, XOAUTH2, UIDL and RETR of a fresh message pass; a new message can take a few seconds to show, so the runner reconnects until it appears (90 s limit).

Outlook.com:

- Basic authentication is off; IMAP/POP/SMTP need OAuth with the scopes above. Personal accounts can no longer register Entra apps, so a client id has to come from an existing registration. Not run.
- Both providers rewrite or add headers (`Received`, `ARC-*`, `Authentication-Results`); the runner counts these as added and fails only when a header we sent changes or the body bytes differ.

## Status (2026-11-04)

| Provider | Result |
|---|---|
| Gmail | 28 rows: 27 pass, 1 not-offered (QRESYNC: not advertised), 0 FAIL. Table in `compatibility.md`. |
| Outlook.com | not run (no OAuth client) |
| Yahoo, iCloud | not run (no accounts) |
