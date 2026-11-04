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
dotnet run --project tools/providers -- token gmail|outlook      # one-time consent; prints the URL or device code
dotnet run --project tools/providers -- smoke gmail|outlook [--protocols imap,smtp,pop] [--trace] [--out rows.md]
```

Everything comes from environment variables or the env file named by `ONEMAIL_PROVIDERS_ENV` (the process environment wins):

| Variable | Provider | Meaning |
|---|---|---|
| `GMAIL_ADDRESS`, `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET` | Gmail | the mailbox, the Desktop client (the secret is what Google's token endpoint expects) |
| `MS_ADDRESS`, `MS_CLIENT_ID`, `MS_TENANT` | Outlook.com | the mailbox, the public client, the tenant segment (default `consumers`) |
| `ONEMAIL_PROVIDERS_TOKENS` | both | the refresh-token file (`GOOGLE_REFRESH_TOKEN`, `MS_REFRESH_TOKEN`); default `tokens.env` beside the env file, created mode 600 |

Without the credentials `smoke` prints "skipped" and exits 77; nothing else runs. It sends only to the account itself (tens of messages: one per auth mechanism and security mode, plus an attachment message, an SMTPUTF8 message where offered and a 5 MB message), tags every message `X-OneMail-Smoke: onemail-smoke-<run>`, and always ends with a cleanup that finds the tag in every selectable folder, moves it to Trash and deletes it there. Re-running is safe, and a run also removes leftovers from earlier aborted runs. Rows are `pass`, `FAIL` or `not-offered` (the server did not advertise the capability); the wire trace (`--trace`, stderr) masks tokens, long base64 words and the address.

What each row checks: SMTP auth+send per mechanism and port; IMAP login per mechanism, LIST with SPECIAL-USE, SELECT, FETCH `BODY[]` compared with the bytes sent (body bytes equal, every sent header intact; added trace headers counted), SEARCH, STORE flags, CONDSTORE, QRESYNC, APPEND, MOVE, IDLE (a message sent over SMTP must raise EXISTS within 90 s), COMPRESS=DEFLATE; POP3 XOAUTH2, UIDL, RETR.

## Quirks

Recorded here as runs find them. Known from the providers' documentation and expected to matter:

- Gmail's IMAP folders are labels: expunging in INBOX only removes the label, so cleanup moves to Trash and deletes there; `[Gmail]/All Mail` and the starred/important views are skipped because they show the same messages.
- Gmail lists POP messages once (recent mode vs. all mail is an account setting); a test message may not appear in POP3 if POP is off for it.
- Outlook.com personal accounts allow IMAP/POP/SMTP OAuth only for the scopes above; basic authentication is off.
- Both providers rewrite or add headers (`Received`, `ARC-*`, `Authentication-Results`, sometimes `Return-Path`); the runner counts these as added and fails only when a header we sent changes or the body bytes differ.

## Status

Not run yet: the test mailboxes and OAuth clients did not exist when the runner was written. Yahoo and iCloud: no accounts, not run.
