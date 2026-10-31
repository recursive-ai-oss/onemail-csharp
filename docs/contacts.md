# Contacts: the vCard typed view, Outlook contacts, and MS-OXVCARD

Three pieces, each usable alone:

- `VCardView` (`RecursiveAI.OneMail.Calendar.VCard`, package `.Calendar`): typed, read-only accessors over a `VCARD` component. Nothing is
  copied out of the tree; the bytes stay in the component, so a card written back untouched is byte-identical.
- `MapiContact` and `MapiDistributionList` (`RecursiveAI.OneMail.Contacts`): typed views over the property bag of an `IPM.Contact` or
  `IPM.DistList` message. They also hang off the shared view as `MapiMailItem.Contact` / `.DistributionList`.
- `ContactToVCardConverter` and `VCardToContactConverter` (`RecursiveAI.OneMail.Conversion.OxVCard`): MS-OXVCARD in both directions, as
  `IItemConversion` with a loss report (`docs/conversion.md`). Family names in the report: `mapi` for contact properties, `vcard` for card
  properties.

## The vCard typed view

`VCardView` reads vCard 2.1, 3.0 and 4.0 alike. Accessors for a property that may repeat return every occurrence in file order
(`Names`, `Titles`, `Roles`, `Notes`, `Organizations`, `Birthdays`, `Anniversaries`); the singular ones (`Name`, `Note`, `Birthday`) are the first.

- **Typed values** (`VCardTyped`): `Value`, `Types` (bare 2.1 parameters and `TYPE=` merged), `Pref`, `AltId`, `Pid`, `Language`,
  `ValueType`, `Label`, `HasType(word)`. Used by `Emails`, `Phones`, `Addresses`, `Urls`, `Impps`, `Related`, `Languages`, `TimeZones`.
- **Media** (`VCardMedia`): `PHOTO`, `LOGO`, `SOUND`, `KEY`. `IsInline`, `Uri`, `MediaType`; `Data` is decoded the first time it is read, not
  at parse time. Base64 is read leniently: surplus or missing `=` padding is accepted, a character outside the alphabet makes `Data` null (the
  raw value is still on the property). A `data:` URI is inline.
- **Dates**: `Birthday`, `Anniversary` are `VCardDate` (year, month, day and optional time, each possibly absent: `--0412`, `1980-03`). A
  `VALUE=text` date, or free text, is returned by `BirthdayText` / `AnniversaryText` and `Birthday` is null. `RevisionTime` is the `REV` instant.
- **Others**: `Gender` (sex and identity text), `Geos` (3.0 `lat;lon`, 4.0 `geo:` URI), `Kind`, `Members`, `Extensions` (`X-` properties, name
  and value), `GetText(property)` and `Unescape(text)` for a property the view has no accessor for.
- **Text unescaping.** In 3.0 and 4.0 a backslash before any character other than `\\ \; \, n N` is dropped and the character kept; 2.1 uses
  `\\` and `\;` only and keeps everything else (ambiguity CV1). The content-line layer is unchanged: the raw value always keeps every backslash.

## The Outlook contact view

`MapiContact(MapiMessage)` reads the properties of MS-OXOCNTC. Strings that are absent and strings that are empty both read as null.

| Area | Members | Properties |
|---|---|---|
| Names | `DisplayName`, `FileUnder`, `GivenName`, `MiddleName`, `Surname`, `Initials`, `NamePrefix`, `NameSuffix`, `Nickname`, `Gender` | `PidTagDisplayName`, `PidLidFileUnder`, `PidTagGivenName` ... `PidTagGeneration`, `PidTagNickname`, `PidTagGender` |
| Work | `CompanyName`, `Department`, `JobTitle`, `Profession`, `OfficeLocation`, `ManagerName`, `AssistantName` | `PidTagCompanyName` ... `PidTagAssistant` |
| Personal | `SpouseName`, `ChildrensNames`, `Hobbies`, `Notes`, `Categories`, `BirthDate`, `AnniversaryDate`, `Birthday`, `Anniversary` | `PidTagSpouseName`, `PidTagChildrensNames`, `PidTagHobbies`, `PidTagBody`, `PidNameKeywords`, `PidTagBirthday`, `PidLidBirthdayLocal`, ... |
| Addresses | `Address(kind)`, `Addresses`, `MailingAddress` | home `PidTagHomeAddress*`; business `PidLidWorkAddress*` (0x8045-0x804A); other `PidTagOtherAddress*`; `PidLidPostalAddressId` selects the mailing address. 0x3A26-0x3A2B are the mailing-address copy and are not read as an address of their own |
| Phones | `Phones`, `Phone(kind)` | the 19 telephone and fax properties in `ContactPhoneKind` |
| Email and fax | `EmailAddresses`, `FaxAddresses` | `PidLidEmail1..3*`, `PidLidFax1..3*`; `SmtpAddress` is the address when the type is `SMTP` (or untyped and address-shaped), null for `EX` and other types |
| Other | `InstantMessagingAddress`, `PersonalHomePage`, `BusinessHomePage`, `FreeBusyUrl`, `UserFields` (4), `PhotoAttachment`, `HasPicture`, `X509Certificates` | |

**Birthday and anniversary** are dates. Outlook writes two properties: `PidTagBirthday` at 11:59 UTC of the date, and `PidLidBirthdayLocal` at
0:00 of the date. The local one is preferred; with only the first, the UTC date is taken. (A reader that shows the instant in a time zone west of
UTC shows the evening before; that is the reader's choice, not a different date.)

`MapiDistributionList(MapiMessage)` reads `PidLidDistributionListName` and the members. When `PidLidDistributionListStream` (0x8064) is present
it is the list of members (MS-OXOCNTC 2.2.2.1); otherwise the multi-valued `PidLidDistributionListMembers` (0x8055) with the one-off entries
(0x8054) are used. A member is a `DistributionListMember`: `Kind` (`OneOff`, `Contact` for a wrapped entry ID of a contact slot, or a list),
`DisplayName`, `AddressType`, `Address`, `SmtpAddress`, `ContactSlot`. A malformed entry is skipped, never thrown.

## Contact to vCard (`ContactToVCardConverter`)

Options: `Version` (3.0 default, as MS-OXVCARD specifies; 4.0), `IncludePhoto` (true).

| Contact | vCard |
|---|---|
| display name, else normalised subject, else the name parts joined | `FN` |
| surname; given; middle; prefix; suffix | `N` |
| nickname | `NICKNAME` |
| photo attachment (bmp, gif, jpeg, png only) | `PHOTO` (3.0 `ENCODING=b;TYPE=`; 4.0 `data:` URI) |
| birthday, anniversary | `BDAY`; `X-MS-ANNIVERSARY` (3.0) or `ANNIVERSARY` (4.0) |
| three addresses | `ADR` with `TYPE=home|work|postal`; 3.0 also `LABEL`; the mailing address has `pref` (3.0) or `PREF=1` (4.0) |
| phones | `TEL` with `TYPE=work|home|cell|car|pager|isdn|fax...`; company, assistant, callback, radio, TTY/TDD as `X-MS-TEL;TYPE=`; telex as `EMAIL;TYPE=tlx` |
| SMTP email addresses (first is preferred) | `EMAIL;TYPE=internet` |
| job title, profession, company and department, categories, body, web pages, free/busy URL | `TITLE`, `ROLE`, `ORG`, `CATEGORIES`, `NOTE`, `URL;TYPE=home|work`, `FBURL` |
| last modification time | `REV` |
| sensitivity | `CLASS` (3.0 only) |
| X.509 certificates | `KEY` |
| children, user fields, IM address, spouse, manager, assistant, hobbies, default postal address | `X-MS-CHILD`, `X-MS-TEXT`, `X-MS-IMADDRESS`, `X-MS-SPOUSE`/`MANAGER`/`ASSISTANT` (`TYPE=N`), `X-MS-INTERESTS`, `X-MS-OL-DEFAULT-POSTAL-ADDRESS` |
| gender (4.0) | `GENDER` |
| distribution list (4.0) | `KIND:group`, one `MEMBER:mailto:` per member with an SMTP address |

Text is escaped for 3.0/4.0 and a CR LF becomes `\n`. Every property of the source is either written or in the report. Codes:

| Code | Kind | Meaning |
|---|---|---|
| `NoVCardMapping` | Dropped | MS-OXVCARD maps nothing from this property |
| `NotInThisVersion` | Dropped | 3.0 has no `GENDER` or group; 4.0 has no `CLASS` |
| `ExchangeAddressNotExported` | Dropped | an `EX` address with no SMTP form |
| `AdditionalValuesNotCarried`, `UnsupportedPhotoType`, `AttachmentNotCarried`, `MemberNotCarried` | Dropped | a second value; a photo that is not bmp/gif/jpeg/png; any other attachment; a list member without SMTP address |
| `PhoneSlotChanged`, `EmailSlotChanged` | Approximated | a second number or an address after a gap moves up when imported: a vCard has no slot order |
| `EmptyValueNotWritten` | Internal | the property is the empty string; a vCard property is not written for it (an empty and an absent text mean the same), and it is absent after import |
| `DefaultValueNotWritten` | Internal | `PidLidPostalAddressId` is 0 (no mailing address) |
| `DerivedValue`, `StoreBookkeeping` | Internal | copies of the display name, formatted addresses, entry IDs, flags |

## vCard to contact (`VCardToContactConverter`)

The result is an `IPM.Contact` message; `PidTagMessageClass`, `PidTagSensitivity` (`CLASS`, default 0) and the derived properties of the
next section are always set. The rules follow the examples of MS-OXVCARD 2.1.3 literally:

- `TEL`: every rule that matches fires. `TYPE=VOICE,MSG,WORK` fills Business and Other; a fax alone fills Primary Fax; `pref` fills Primary; no type fills Other. The second work (home) number goes to Business2 (Home2).
- `ADR`: with no recognised type, Business and Other; `postal`, `dom`, `intl`, `parcel` fill Other. `pref` sets `PidLidPostalAddressId`. A fourth address is `AdditionalValueNotMapped`; the extended-address part is merged into the street (`AddressExtendedMerged`).
- `EMAIL`: the first three fill the email slots (`PidLidEmail1..3`); more are `AdditionalValueNotMapped`.
- `BDAY`: `PidTagBirthday` is the date at 11:59 UTC, `PidLidBirthdayLocal` the date at 0:00 (`TimeOfDayDropped` when the card had a time). A partial date is `PartialDate` and not stored; `X-MS-ANNIVERSARY` and 4.0 `ANNIVERSARY` likewise.
- `N` components written as lists are stored as the comma-joined text; several organisation units are joined with ", " (`OrgUnitsMerged`).
- `PHOTO`: inline data of a supported type becomes the contact photo attachment; a URI is `PhotoNotInline`. `KEY` that is not X.509 is `KeyNotX509`.
- Properties with no home (`UID`, `GEO`, `TZ`, `LANG`, `IMPP` beyond the first, ...) are `NoContactMapping`; `VERSION`, `PRODID` and `MAILER` are `VCardBookkeeping`.

**Derived on import** (never a loss): `PidTagSubject`, `PidTagNormalizedSubject`, `PidTagConversationTopic` (copies of the display name),
`PidTagDisplayName` when the card has no `FN` (composed from `N`), `PidLidEmailNDisplayName` and the formatted addresses, the mailing-address copy
(`PidTagPostalAddress`, 0x3A26-0x3A2B), `PidLidFileUnder` / `PidLidFileUnderId`, `PidTagMessageClass`, and flags.

## Fidelity

- Contact → vCard → contact: each mapped property comes back equal, or the report names it. "Equal" is the exact property value; what the
  check does not count as a loss is the derived set above.
- vCard → contact → vCard: the contact is stable on a second round; the first differs from the source only in the derived additions (an `FN`
  composed from `N`) and in what the report lists.

## Checks

`tools/contacts` holds them; nothing there ships in a package.

| Tool | What it checks |
|---|---|
| `ContactTool dump / export / rt-contact / rt-vcard / vfacts / gen` | reads every contact and list item of MSG/PST/OST; converts both ways; counts exact, reported, derived, mismatch, silent |
| `ContactTool gen DIR [seed [count]]` | seeded contact and list MSG files covering the MS-OXOCNTC properties (empty strings, odd characters, slot gaps, photos) |
| `make_vcards.py` | seeded vCards 2.1/3.0/4.0 (QP, groups, partial dates, photos, `KIND:group`) |
| `compare_extract_msg.py` | contact fields against extract-msg 0.55.0 |
| `compare_vcard.py` + `calendar/oracles` | typed view against vobject 0.9.9 and FolkerKinzel.VCards 8.2.0 (rule classes OL-*, V21COMMA, ESC, K1) |
| `run.sh`, `vcard_checks.sh` | the worker runs of all of the above |

Results are in [compatibility.md](compatibility.md). The fuzz target is `vcard-view` (a vCard, or a damaged contact or list, through the view and
both conversions; the vCard written must parse again).
