# Fidelity: what carries over when an item changes format

`Mail.ConvertAsync` and `StoreConverter` read an item of any family and write it in another format. Every conversion returns a
loss report (`docs/conversion.md`); this page says, per source family and target, what to expect from it. The tables below are
generated from `FidelityMatrix` in the library, and a test converts items of every family into every target and fails when a report
holds a code its cell does not list, when a cell says "skipped" for something that converts (or the other way round), or when this
page differs from the matrix.

| Level | Meaning |
|---|---|
| Exact | the same bytes, or the same values |
| Derived | recomputed in the target's own form by a rule of the specification; reading it back gives the same meaning |
| Approximated | carried in a weaker form; every case is a loss-report entry |
| Lost | not carried; every case is a loss-report entry, or the item is skipped and counted with the reason shown |
| None | the source has nothing of this kind |

The columns: **Content** is headers, addresses, bodies and attachments (for calendar and contact items, their fields); **Properties**
is what the source holds that the target has no field for (MAPI properties without an Internet form, OLM elements nothing reads);
**Flags** is read, answered, flagged and draft state plus keywords or categories; **Folders** is the folder the item was in.

## Store to store

`StoreConverter` enumerates a source one item at a time and hands each converted item to an `IStoreSink`. Sinks exist for:

| Destination | Layout | Flags | Dates |
|---|---|---|---|
| `Eml` | `<folder>/<n>-<subject>.eml` | not kept (`FlagsNotCarried`) | file time = arrival date |
| `Mbox` | one `<folder>.mbox` per folder (root items in `_root.mbox`); a destination with an extension is one file | `Status`/`X-Status`/`X-Keywords` fields, added only to a message that has none | envelope date = arrival date, else sent date, else the Unix epoch (`EnvelopeDateUnknown`) |
| `Maildir` | Maildir++ (`.Parent.Child`); a dot inside a folder name becomes `_` | standard flags and up to 26 keywords per folder | file time = arrival date |
| `AppleMail` | `<folder>.mbox/Messages/<n>.emlx` | the `.emlx` flags word; keywords not kept | `date-received` and `date-sent` |
| `Ics`, `JCal`, `XCal`, `VCard`, `JCard`, `XCard`, `Tnef` | one file per item under the folder tree | none | file time |

Calendar and contact items that reach a mail store are written as `.ics` and `.vcf` files in an `_items` directory beside it
(`OtherItemsMode.Route`, the default) or skipped (`OtherItemsMode.Skip`). An item the target cannot hold (a contact for an
iCalendar target, a message for a card target) is skipped, counted under its reason, and the totals always satisfy
items read = items written + items skipped.

Source folders: PST/OST folder paths below the root, OLM folder paths, Maildir++ levels, Apple Mail bundle nesting, and, for a
directory that is not a store, the directory names (a file holding many items adds its own name). `FolderFilter` restricts a run to one subtree.

A cancelled run finishes its destination, so what was written reads back whole; the report holds the counts so far. A failure of the
destination stops the run; an item that cannot be read or converted is skipped as `Failed` and listed in `Errors`.

## The matrix

<!-- matrix:start -->
### MIME message (EML, mbox, Maildir, EMLX, DBX, MHTML)

| To | Content | Properties | Flags | Folders | Codes it can report | Note |
|---|---|---|---|---|---|---|
| Eml | Exact | None | Lost | Derived | `FlagsNotCarried`, `FolderNameChanged`, `KeywordsNotCarried` | One .eml file per message; folders become directories. A file has no flags, so they are reported. |
| Mbox | Exact | None | Derived | Derived | `EnvelopeDateUnknown`, `FinalLineEndingAdded`, `FlagsNotCarried`, `FlagsWrittenAsHeaders`, `FolderFlattened`, `FolderNameChanged` | Flags that the message does not state itself are written as Status, X-Status and X-Keywords fields in front of it; the only other case in which bytes change is a message with no final line ending, which gains one. One file per folder; a single-file destination drops folders. |
| Maildir | Exact | None | Exact | Derived | `FlagsNotCarried`, `FolderNameChanged`, `KeywordsNotCarried` | Flags and up to 26 keywords per folder go into the file name; Maildir++ folder names cannot hold a dot. |
| AppleMail | Exact | None | Derived | Derived | `FlagsNotCarried`, `FolderNameChanged`, `KeywordsNotCarried` | Flags go into the .emlx property list; keywords have no place there. |
| Tnef | Approximated | None | None | Lost | `DateZoneNotStored`, `DefaultValueNotWritten`, `EmbeddedMessageTooDeep`, `EmptyValueNotWritten`, `ExtraAuthorsNotMapped`, `GroupFlattened`, `InReplyToFirstOnly`, `InvalidMediaType`, `NamedPropertyValueSetAside`, `PartHeaderNotMapped`, `PartsTooDeep`, `RepeatedHeaderNotMapped`, `SignatureNotVerifiable`, `StoreBookkeeping`, `ThreadIndexNotBase64`, `TnefCodePageDiffers`, `TnefMessageClassUsed`, `TnefNotUsed`, `TnefPartUnpacked`, `TnefPropertyIgnored`, `TnefPropertyWithoutName`, `TnefRecipientIgnored`, `TnefTrailingCrLfIgnored`, `TnefWriterLimit`, `ValueOutOfRange` | The message is converted to an Outlook item (MS-OXCMAIL) and written as TNEF. |
| Ics | Lost | None | None | Lost | skipped: `NotCalendarItem` | A message is not a calendar item; it is skipped and counted. |
| JCal | Lost | None | None | Lost | skipped: `NotCalendarItem` | A message is not a calendar item; it is skipped and counted. |
| XCal | Lost | None | None | Lost | skipped: `NotCalendarItem` | A message is not a calendar item; it is skipped and counted. |
| VCard | Lost | None | None | Lost | skipped: `NotContactItem` | A message is not a contact; it is skipped and counted. |
| JCard | Lost | None | None | Lost | skipped: `NotContactItem` | A message is not a contact; it is skipped and counted. |
| XCard | Lost | None | None | Lost | skipped: `NotContactItem` | A message is not a contact; it is skipped and counted. |

### Outlook mail (MSG, OFT, PST, OST, TNEF)

| To | Content | Properties | Flags | Folders | Codes it can report | Note |
|---|---|---|---|---|---|---|
| Eml | Derived | Lost | Lost | Derived | `AddressSpellingFromTransportHeader`, `AddressUnresolved`, `AttachmentByReference`, `AttachmentWithoutData`, `BccNotWritten`, `BodyLineBreaksNormalized`, `CompositeTypeOnFlatData`, `DateBeforeFileTimeEpoch`, `DateFromDeliveryTime`, `DateSubSecondDropped`, `DefaultValueNotWritten`, `EmbeddedMessageTooDeep`, `EmptyValueNotWritten`, `ExchangeAddressWrittenAsSmtp`, `FlagsNotCarried`, `FolderNameChanged`, `HtmlFromRtf`, `InvalidMediaType`, `KeywordsNotCarried`, `MessageClassNotCarried`, `NamedPropertyValueSetAside`, `NoInternetMapping`, `ReplyToUnresolved`, `RtfCarriedAsTextRtf`, `RtfRedundant`, `RtfUndecodable`, `StoreBookkeeping`, `SubjectPrefixMarkerRemoved`, `SubjectWhitespaceTrimmed`, `TextFromRtf`, `TransportHeaderLineSkipped`, `TransportHeaderOverridden`, `TransportHeadersRewritten`, `ValueOutOfRange` | MS-OXCMAIL 2.1 builds the message from the properties; properties with no Internet form are reported. Flags come from PidTagMessageFlags (read, unsent), PidTagFlagStatus and PidTagLastVerbExecuted. |
| Mbox | Derived | Lost | Derived | Derived | `AddressSpellingFromTransportHeader`, `AddressUnresolved`, `AttachmentByReference`, `AttachmentWithoutData`, `BccNotWritten`, `BodyLineBreaksNormalized`, `CompositeTypeOnFlatData`, `DateBeforeFileTimeEpoch`, `DateFromDeliveryTime`, `DateSubSecondDropped`, `DefaultValueNotWritten`, `EmbeddedMessageTooDeep`, `EmptyValueNotWritten`, `EnvelopeDateUnknown`, `ExchangeAddressWrittenAsSmtp`, `FinalLineEndingAdded`, `FlagsNotCarried`, `FlagsWrittenAsHeaders`, `FolderFlattened`, `FolderNameChanged`, `HtmlFromRtf`, `InvalidMediaType`, `KeywordsNotCarried`, `MessageClassNotCarried`, `NamedPropertyValueSetAside`, `NoInternetMapping`, `ReplyToUnresolved`, `RtfCarriedAsTextRtf`, `RtfRedundant`, `RtfUndecodable`, `StoreBookkeeping`, `SubjectPrefixMarkerRemoved`, `SubjectWhitespaceTrimmed`, `TextFromRtf`, `TransportHeaderLineSkipped`, `TransportHeaderOverridden`, `TransportHeadersRewritten`, `ValueOutOfRange` | MS-OXCMAIL 2.1 builds the message from the properties; properties with no Internet form are reported. Flags come from PidTagMessageFlags (read, unsent), PidTagFlagStatus and PidTagLastVerbExecuted. |
| Maildir | Derived | Lost | Derived | Derived | `AddressSpellingFromTransportHeader`, `AddressUnresolved`, `AttachmentByReference`, `AttachmentWithoutData`, `BccNotWritten`, `BodyLineBreaksNormalized`, `CompositeTypeOnFlatData`, `DateBeforeFileTimeEpoch`, `DateFromDeliveryTime`, `DateSubSecondDropped`, `DefaultValueNotWritten`, `EmbeddedMessageTooDeep`, `EmptyValueNotWritten`, `ExchangeAddressWrittenAsSmtp`, `FlagsNotCarried`, `FolderNameChanged`, `HtmlFromRtf`, `InvalidMediaType`, `KeywordsNotCarried`, `MessageClassNotCarried`, `NamedPropertyValueSetAside`, `NoInternetMapping`, `ReplyToUnresolved`, `RtfCarriedAsTextRtf`, `RtfRedundant`, `RtfUndecodable`, `StoreBookkeeping`, `SubjectPrefixMarkerRemoved`, `SubjectWhitespaceTrimmed`, `TextFromRtf`, `TransportHeaderLineSkipped`, `TransportHeaderOverridden`, `TransportHeadersRewritten`, `ValueOutOfRange` | MS-OXCMAIL 2.1 builds the message from the properties; properties with no Internet form are reported. Flags come from PidTagMessageFlags (read, unsent), PidTagFlagStatus and PidTagLastVerbExecuted. |
| AppleMail | Derived | Lost | Derived | Derived | `AddressSpellingFromTransportHeader`, `AddressUnresolved`, `AttachmentByReference`, `AttachmentWithoutData`, `BccNotWritten`, `BodyLineBreaksNormalized`, `CompositeTypeOnFlatData`, `DateBeforeFileTimeEpoch`, `DateFromDeliveryTime`, `DateSubSecondDropped`, `DefaultValueNotWritten`, `EmbeddedMessageTooDeep`, `EmptyValueNotWritten`, `ExchangeAddressWrittenAsSmtp`, `FlagsNotCarried`, `FolderNameChanged`, `HtmlFromRtf`, `InvalidMediaType`, `KeywordsNotCarried`, `MessageClassNotCarried`, `NamedPropertyValueSetAside`, `NoInternetMapping`, `ReplyToUnresolved`, `RtfCarriedAsTextRtf`, `RtfRedundant`, `RtfUndecodable`, `StoreBookkeeping`, `SubjectPrefixMarkerRemoved`, `SubjectWhitespaceTrimmed`, `TextFromRtf`, `TransportHeaderLineSkipped`, `TransportHeaderOverridden`, `TransportHeadersRewritten`, `ValueOutOfRange` | MS-OXCMAIL 2.1 builds the message from the properties; properties with no Internet form are reported. Flags come from PidTagMessageFlags (read, unsent), PidTagFlagStatus and PidTagLastVerbExecuted. |
| Tnef | Exact | Exact | Exact | Lost | `TnefWriterLimit` | TNEF carries every property; a type or an OLE object it cannot hold is reported by name. |
| Ics | Lost | None | None | Lost | skipped: `NotCalendarItem` | Not an item of that family; it is skipped and counted. |
| JCal | Lost | None | None | Lost | skipped: `NotCalendarItem` | Not an item of that family; it is skipped and counted. |
| XCal | Lost | None | None | Lost | skipped: `NotCalendarItem` | Not an item of that family; it is skipped and counted. |
| VCard | Lost | None | None | Lost | skipped: `NotContactItem` | Not an item of that family; it is skipped and counted. |
| JCard | Lost | None | None | Lost | skipped: `NotContactItem` | Not an item of that family; it is skipped and counted. |
| XCard | Lost | None | None | Lost | skipped: `NotContactItem` | Not an item of that family; it is skipped and counted. |

### Outlook appointment, meeting or task (MSG, PST, OST, TNEF)

| To | Content | Properties | Flags | Folders | Codes it can report | Note |
|---|---|---|---|---|---|---|
| Eml | Approximated | Lost | None | Derived | `AmbiguousFirstInstance`, `AmbiguousShortMonth`, `AttachmentNotCarried`, `AttachmentWithoutException`, `CountOutOfRange`, `DerivedProperty`, `EndMismatch`, `ExceptionWithoutAttachment`, `FlagsNotCarried`, `FolderNameChanged`, `ForwardNotificationHasNoMethod`, `InconsistentTimeZone`, `KeywordsNotCarried`, `MessageClassNotCarried`, `MissingTime`, `NoIcalMapping`, `OrganizerWithoutSmtpAddress`, `RecurrenceNotCarried`, `RtfBodyNotCarried`, `StoreBookkeeping`, `TaskStatusWeakened`, `TrailingBytes`, `Truncated`, `UidSynthesized`, `UnexpectedVersion`, `UnsupportedCalendar`, `ValueOutOfRange` | Routed to an .ics file in the side directory (or skipped with OtherItemsMode.Skip) by MS-OXCICAL. |
| Mbox | Approximated | Lost | None | Derived | `AmbiguousFirstInstance`, `AmbiguousShortMonth`, `AttachmentNotCarried`, `AttachmentWithoutException`, `CountOutOfRange`, `DerivedProperty`, `EndMismatch`, `ExceptionWithoutAttachment`, `FlagsNotCarried`, `FolderNameChanged`, `ForwardNotificationHasNoMethod`, `InconsistentTimeZone`, `KeywordsNotCarried`, `MessageClassNotCarried`, `MissingTime`, `NoIcalMapping`, `OrganizerWithoutSmtpAddress`, `RecurrenceNotCarried`, `RtfBodyNotCarried`, `StoreBookkeeping`, `TaskStatusWeakened`, `TrailingBytes`, `Truncated`, `UidSynthesized`, `UnexpectedVersion`, `UnsupportedCalendar`, `ValueOutOfRange` | Routed to an .ics file in the side directory (or skipped with OtherItemsMode.Skip) by MS-OXCICAL. |
| Maildir | Approximated | Lost | None | Derived | `AmbiguousFirstInstance`, `AmbiguousShortMonth`, `AttachmentNotCarried`, `AttachmentWithoutException`, `CountOutOfRange`, `DerivedProperty`, `EndMismatch`, `ExceptionWithoutAttachment`, `FlagsNotCarried`, `FolderNameChanged`, `ForwardNotificationHasNoMethod`, `InconsistentTimeZone`, `KeywordsNotCarried`, `MessageClassNotCarried`, `MissingTime`, `NoIcalMapping`, `OrganizerWithoutSmtpAddress`, `RecurrenceNotCarried`, `RtfBodyNotCarried`, `StoreBookkeeping`, `TaskStatusWeakened`, `TrailingBytes`, `Truncated`, `UidSynthesized`, `UnexpectedVersion`, `UnsupportedCalendar`, `ValueOutOfRange` | Routed to an .ics file in the side directory (or skipped with OtherItemsMode.Skip) by MS-OXCICAL. |
| AppleMail | Approximated | Lost | None | Derived | `AmbiguousFirstInstance`, `AmbiguousShortMonth`, `AttachmentNotCarried`, `AttachmentWithoutException`, `CountOutOfRange`, `DerivedProperty`, `EndMismatch`, `ExceptionWithoutAttachment`, `FlagsNotCarried`, `FolderNameChanged`, `ForwardNotificationHasNoMethod`, `InconsistentTimeZone`, `KeywordsNotCarried`, `MessageClassNotCarried`, `MissingTime`, `NoIcalMapping`, `OrganizerWithoutSmtpAddress`, `RecurrenceNotCarried`, `RtfBodyNotCarried`, `StoreBookkeeping`, `TaskStatusWeakened`, `TrailingBytes`, `Truncated`, `UidSynthesized`, `UnexpectedVersion`, `UnsupportedCalendar`, `ValueOutOfRange` | Routed to an .ics file in the side directory (or skipped with OtherItemsMode.Skip) by MS-OXCICAL. |
| Ics | Approximated | Lost | None | Lost | `AmbiguousFirstInstance`, `AmbiguousShortMonth`, `AttachmentNotCarried`, `AttachmentWithoutException`, `CountOutOfRange`, `DerivedProperty`, `EndMismatch`, `ExceptionWithoutAttachment`, `ForwardNotificationHasNoMethod`, `InconsistentTimeZone`, `MessageClassNotCarried`, `MissingTime`, `NoIcalMapping`, `OrganizerWithoutSmtpAddress`, `RecurrenceNotCarried`, `RtfBodyNotCarried`, `StoreBookkeeping`, `TaskStatusWeakened`, `TrailingBytes`, `Truncated`, `UidSynthesized`, `UnexpectedVersion`, `UnsupportedCalendar`, `ValueOutOfRange` | MS-OXCICAL: recurrence, exceptions, attendees and reminders carry; properties iCalendar has no field for are reported. |
| JCal | Approximated | Lost | None | Lost | `AmbiguousFirstInstance`, `AmbiguousShortMonth`, `AttachmentNotCarried`, `AttachmentWithoutException`, `CountOutOfRange`, `DerivedProperty`, `EndMismatch`, `ExceptionWithoutAttachment`, `FormAmbiguousValueType`, `FormBareParameterAsType`, `FormCharsetRemoved`, `FormComponentNotCarried`, `FormDuplicateParameterMerged`, `FormExtraComponentsDropped`, `FormFolding`, `FormGroupNotCarried`, `FormInlineBinaryAsDataUri`, `FormInvalidXmlCharacter`, `FormItemOrder`, `FormLineNotCarried`, `FormNameCase`, `FormParameterNotCarried`, `FormPropertyNotCarried`, `FormRedundantValueParameter`, `FormStructuredPadded`, `FormTransferEncodingRemoved`, `FormTypeNameNotCarried`, `FormValueNotRepresentable`, `FormVersionNotCarried`, `FormVersionSpecificForm`, `ForwardNotificationHasNoMethod`, `InconsistentTimeZone`, `MessageClassNotCarried`, `MissingTime`, `NoIcalMapping`, `OrganizerWithoutSmtpAddress`, `RecurrenceNotCarried`, `RtfBodyNotCarried`, `StoreBookkeeping`, `TaskStatusWeakened`, `TrailingBytes`, `Truncated`, `UidSynthesized`, `UnexpectedVersion`, `UnsupportedCalendar`, `ValueOutOfRange` | MS-OXCICAL: recurrence, exceptions, attendees and reminders carry; properties iCalendar has no field for are reported. |
| XCal | Approximated | Lost | None | Lost | `AmbiguousFirstInstance`, `AmbiguousShortMonth`, `AttachmentNotCarried`, `AttachmentWithoutException`, `CountOutOfRange`, `DerivedProperty`, `EndMismatch`, `ExceptionWithoutAttachment`, `FormAmbiguousValueType`, `FormBareParameterAsType`, `FormCharsetRemoved`, `FormComponentNotCarried`, `FormDuplicateParameterMerged`, `FormExtraComponentsDropped`, `FormFolding`, `FormGroupNotCarried`, `FormInlineBinaryAsDataUri`, `FormInvalidXmlCharacter`, `FormItemOrder`, `FormLineNotCarried`, `FormNameCase`, `FormParameterNotCarried`, `FormPropertyNotCarried`, `FormRedundantValueParameter`, `FormStructuredPadded`, `FormTransferEncodingRemoved`, `FormTypeNameNotCarried`, `FormValueNotRepresentable`, `FormVersionNotCarried`, `FormVersionSpecificForm`, `ForwardNotificationHasNoMethod`, `InconsistentTimeZone`, `MessageClassNotCarried`, `MissingTime`, `NoIcalMapping`, `OrganizerWithoutSmtpAddress`, `RecurrenceNotCarried`, `RtfBodyNotCarried`, `StoreBookkeeping`, `TaskStatusWeakened`, `TrailingBytes`, `Truncated`, `UidSynthesized`, `UnexpectedVersion`, `UnsupportedCalendar`, `ValueOutOfRange` | MS-OXCICAL: recurrence, exceptions, attendees and reminders carry; properties iCalendar has no field for are reported. |
| Tnef | Exact | Exact | Exact | Lost | `TnefWriterLimit` | Every property travels in the TNEF. |
| VCard | Lost | None | None | Lost | skipped: `NotContactItem` | Not an item of that family; it is skipped and counted. |
| JCard | Lost | None | None | Lost | skipped: `NotContactItem` | Not an item of that family; it is skipped and counted. |
| XCard | Lost | None | None | Lost | skipped: `NotContactItem` | Not an item of that family; it is skipped and counted. |

### Outlook contact (MSG, PST, OST, TNEF)

| To | Content | Properties | Flags | Folders | Codes it can report | Note |
|---|---|---|---|---|---|---|
| Eml | Approximated | Lost | None | Derived | `AttachmentNotCarried`, `DefaultValueNotWritten`, `DerivedValue`, `EmailSlotChanged`, `EmptyValueNotWritten`, `ExchangeAddressNotExported`, `FlagsNotCarried`, `FolderNameChanged`, `KeywordsNotCarried`, `MemberNotCarried`, `NotAContact`, `PhoneSlotChanged`, `StoreBookkeeping`, `UnsupportedPhotoType` | Routed to a .vcf file in the side directory (or skipped) by MS-OXVCARD. |
| Mbox | Approximated | Lost | None | Derived | `AttachmentNotCarried`, `DefaultValueNotWritten`, `DerivedValue`, `EmailSlotChanged`, `EmptyValueNotWritten`, `ExchangeAddressNotExported`, `FlagsNotCarried`, `FolderNameChanged`, `KeywordsNotCarried`, `MemberNotCarried`, `NotAContact`, `PhoneSlotChanged`, `StoreBookkeeping`, `UnsupportedPhotoType` | Routed to a .vcf file in the side directory (or skipped) by MS-OXVCARD. |
| Maildir | Approximated | Lost | None | Derived | `AttachmentNotCarried`, `DefaultValueNotWritten`, `DerivedValue`, `EmailSlotChanged`, `EmptyValueNotWritten`, `ExchangeAddressNotExported`, `FlagsNotCarried`, `FolderNameChanged`, `KeywordsNotCarried`, `MemberNotCarried`, `NotAContact`, `PhoneSlotChanged`, `StoreBookkeeping`, `UnsupportedPhotoType` | Routed to a .vcf file in the side directory (or skipped) by MS-OXVCARD. |
| AppleMail | Approximated | Lost | None | Derived | `AttachmentNotCarried`, `DefaultValueNotWritten`, `DerivedValue`, `EmailSlotChanged`, `EmptyValueNotWritten`, `ExchangeAddressNotExported`, `FlagsNotCarried`, `FolderNameChanged`, `KeywordsNotCarried`, `MemberNotCarried`, `NotAContact`, `PhoneSlotChanged`, `StoreBookkeeping`, `UnsupportedPhotoType` | Routed to a .vcf file in the side directory (or skipped) by MS-OXVCARD. |
| VCard | Approximated | Lost | None | Lost | `AttachmentNotCarried`, `DefaultValueNotWritten`, `DerivedValue`, `EmailSlotChanged`, `EmptyValueNotWritten`, `ExchangeAddressNotExported`, `MemberNotCarried`, `NotAContact`, `PhoneSlotChanged`, `StoreBookkeeping`, `UnsupportedPhotoType` | MS-OXVCARD: names, phones, e-mail slots, addresses, dates and photo carry; properties vCard has no field for are reported. |
| JCard | Approximated | Lost | None | Lost | `AttachmentNotCarried`, `DefaultValueNotWritten`, `DerivedValue`, `EmailSlotChanged`, `EmptyValueNotWritten`, `ExchangeAddressNotExported`, `FormAmbiguousValueType`, `FormBareParameterAsType`, `FormCharsetRemoved`, `FormComponentNotCarried`, `FormDuplicateParameterMerged`, `FormExtraComponentsDropped`, `FormFolding`, `FormGroupNotCarried`, `FormInlineBinaryAsDataUri`, `FormInvalidXmlCharacter`, `FormItemOrder`, `FormLineNotCarried`, `FormNameCase`, `FormParameterNotCarried`, `FormPropertyNotCarried`, `FormRedundantValueParameter`, `FormStructuredPadded`, `FormTransferEncodingRemoved`, `FormTypeNameNotCarried`, `FormValueNotRepresentable`, `FormVersionNotCarried`, `FormVersionSpecificForm`, `MemberNotCarried`, `NotAContact`, `PhoneSlotChanged`, `StoreBookkeeping`, `UnsupportedPhotoType` | MS-OXVCARD: names, phones, e-mail slots, addresses, dates and photo carry; properties vCard has no field for are reported. |
| XCard | Approximated | Lost | None | Lost | `AttachmentNotCarried`, `DefaultValueNotWritten`, `DerivedValue`, `EmailSlotChanged`, `EmptyValueNotWritten`, `ExchangeAddressNotExported`, `FormAmbiguousValueType`, `FormBareParameterAsType`, `FormCharsetRemoved`, `FormComponentNotCarried`, `FormDuplicateParameterMerged`, `FormExtraComponentsDropped`, `FormFolding`, `FormGroupNotCarried`, `FormInlineBinaryAsDataUri`, `FormInvalidXmlCharacter`, `FormItemOrder`, `FormLineNotCarried`, `FormNameCase`, `FormParameterNotCarried`, `FormPropertyNotCarried`, `FormRedundantValueParameter`, `FormStructuredPadded`, `FormTransferEncodingRemoved`, `FormTypeNameNotCarried`, `FormValueNotRepresentable`, `FormVersionNotCarried`, `FormVersionSpecificForm`, `MemberNotCarried`, `NotAContact`, `PhoneSlotChanged`, `StoreBookkeeping`, `UnsupportedPhotoType` | MS-OXVCARD: names, phones, e-mail slots, addresses, dates and photo carry; properties vCard has no field for are reported. |
| Tnef | Exact | Exact | Exact | Lost | `TnefWriterLimit` | Every property travels in the TNEF. |
| Ics | Lost | None | None | Lost | skipped: `NotCalendarItem` | Not an item of that family; it is skipped and counted. |
| JCal | Lost | None | None | Lost | skipped: `NotCalendarItem` | Not an item of that family; it is skipped and counted. |
| XCal | Lost | None | None | Lost | skipped: `NotCalendarItem` | Not an item of that family; it is skipped and counted. |

### Outlook for Mac mail (OLM)

| To | Content | Properties | Flags | Folders | Codes it can report | Note |
|---|---|---|---|---|---|---|
| Eml | Approximated | Lost | Derived | Derived | `BccNotWritten`, `DateZoneNotStored`, `EmbeddedMessageAsData`, `FlagsNotCarried`, `FolderNameChanged`, `KeywordsNotCarried`, `OlmFieldDiffersFromSource`, `Unmapped` | The message is rebuilt from the fields the view reads (addresses, subject, dates, identifiers, bodies, attachments); other OLM elements are listed as Unmapped. The read flag becomes Seen, categories become keywords. |
| Mbox | Approximated | Lost | Derived | Derived | `BccNotWritten`, `DateZoneNotStored`, `EmbeddedMessageAsData`, `EnvelopeDateUnknown`, `FinalLineEndingAdded`, `FlagsNotCarried`, `FlagsWrittenAsHeaders`, `FolderFlattened`, `FolderNameChanged`, `KeywordsNotCarried`, `OlmFieldDiffersFromSource`, `Unmapped` | The message is rebuilt from the fields the view reads (addresses, subject, dates, identifiers, bodies, attachments); other OLM elements are listed as Unmapped. The read flag becomes Seen, categories become keywords. |
| Maildir | Approximated | Lost | Derived | Derived | `BccNotWritten`, `DateZoneNotStored`, `EmbeddedMessageAsData`, `FlagsNotCarried`, `FolderNameChanged`, `KeywordsNotCarried`, `OlmFieldDiffersFromSource`, `Unmapped` | The message is rebuilt from the fields the view reads (addresses, subject, dates, identifiers, bodies, attachments); other OLM elements are listed as Unmapped. The read flag becomes Seen, categories become keywords. |
| AppleMail | Approximated | Lost | Derived | Derived | `BccNotWritten`, `DateZoneNotStored`, `EmbeddedMessageAsData`, `FlagsNotCarried`, `FolderNameChanged`, `KeywordsNotCarried`, `OlmFieldDiffersFromSource`, `Unmapped` | The message is rebuilt from the fields the view reads (addresses, subject, dates, identifiers, bodies, attachments); other OLM elements are listed as Unmapped. The read flag becomes Seen, categories become keywords. |
| Tnef | Lost | Lost | None | Lost | skipped: `NotMapiConvertible` | An OLM record has no property bag; it is skipped and counted. |
| Ics | Lost | None | None | Lost | skipped: `NotCalendarItem` | Not an item of that family; it is skipped and counted. |
| JCal | Lost | None | None | Lost | skipped: `NotCalendarItem` | Not an item of that family; it is skipped and counted. |
| XCal | Lost | None | None | Lost | skipped: `NotCalendarItem` | Not an item of that family; it is skipped and counted. |
| VCard | Lost | None | None | Lost | skipped: `NotContactItem` | Not an item of that family; it is skipped and counted. |
| JCard | Lost | None | None | Lost | skipped: `NotContactItem` | Not an item of that family; it is skipped and counted. |
| XCard | Lost | None | None | Lost | skipped: `NotContactItem` | Not an item of that family; it is skipped and counted. |

### Outlook for Mac appointment, task or note (OLM)

| To | Content | Properties | Flags | Folders | Codes it can report | Note |
|---|---|---|---|---|---|---|
| Eml | Approximated | Lost | None | Derived | `AllDayEnd`, `AttendeeStatus`, `CalendarAttachments`, `DateOnlyZone`, `EndZoneIgnored`, `FlagsNotCarried`, `FloatingTime`, `FolderNameChanged`, `KeywordsNotCarried`, `NotACalendarItem`, `OrganizerNotAnAddress`, `RecurrenceEnd`, `RecurrenceKind`, `RelativeRecurrence`, `Unmapped` | Routed to an .ics file in the side directory (or skipped). |
| Mbox | Approximated | Lost | None | Derived | `AllDayEnd`, `AttendeeStatus`, `CalendarAttachments`, `DateOnlyZone`, `EndZoneIgnored`, `FlagsNotCarried`, `FloatingTime`, `FolderNameChanged`, `KeywordsNotCarried`, `NotACalendarItem`, `OrganizerNotAnAddress`, `RecurrenceEnd`, `RecurrenceKind`, `RelativeRecurrence`, `Unmapped` | Routed to an .ics file in the side directory (or skipped). |
| Maildir | Approximated | Lost | None | Derived | `AllDayEnd`, `AttendeeStatus`, `CalendarAttachments`, `DateOnlyZone`, `EndZoneIgnored`, `FlagsNotCarried`, `FloatingTime`, `FolderNameChanged`, `KeywordsNotCarried`, `NotACalendarItem`, `OrganizerNotAnAddress`, `RecurrenceEnd`, `RecurrenceKind`, `RelativeRecurrence`, `Unmapped` | Routed to an .ics file in the side directory (or skipped). |
| AppleMail | Approximated | Lost | None | Derived | `AllDayEnd`, `AttendeeStatus`, `CalendarAttachments`, `DateOnlyZone`, `EndZoneIgnored`, `FlagsNotCarried`, `FloatingTime`, `FolderNameChanged`, `KeywordsNotCarried`, `NotACalendarItem`, `OrganizerNotAnAddress`, `RecurrenceEnd`, `RecurrenceKind`, `RelativeRecurrence`, `Unmapped` | Routed to an .ics file in the side directory (or skipped). |
| Ics | Approximated | Lost | None | Lost | `AllDayEnd`, `AttendeeStatus`, `CalendarAttachments`, `DateOnlyZone`, `EndZoneIgnored`, `FloatingTime`, `NotACalendarItem`, `OrganizerNotAnAddress`, `RecurrenceEnd`, `RecurrenceKind`, `RelativeRecurrence`, `Unmapped` | Appointments, tasks and notes (VJOURNAL) carry their fields; the rest is listed as Unmapped. |
| JCal | Approximated | Lost | None | Lost | `AllDayEnd`, `AttendeeStatus`, `CalendarAttachments`, `DateOnlyZone`, `EndZoneIgnored`, `FloatingTime`, `FormAmbiguousValueType`, `FormBareParameterAsType`, `FormCharsetRemoved`, `FormComponentNotCarried`, `FormDuplicateParameterMerged`, `FormExtraComponentsDropped`, `FormFolding`, `FormGroupNotCarried`, `FormInlineBinaryAsDataUri`, `FormInvalidXmlCharacter`, `FormItemOrder`, `FormLineNotCarried`, `FormNameCase`, `FormParameterNotCarried`, `FormPropertyNotCarried`, `FormRedundantValueParameter`, `FormStructuredPadded`, `FormTransferEncodingRemoved`, `FormTypeNameNotCarried`, `FormValueNotRepresentable`, `FormVersionNotCarried`, `FormVersionSpecificForm`, `NotACalendarItem`, `OrganizerNotAnAddress`, `RecurrenceEnd`, `RecurrenceKind`, `RelativeRecurrence`, `Unmapped` | Appointments, tasks and notes (VJOURNAL) carry their fields; the rest is listed as Unmapped. |
| XCal | Approximated | Lost | None | Lost | `AllDayEnd`, `AttendeeStatus`, `CalendarAttachments`, `DateOnlyZone`, `EndZoneIgnored`, `FloatingTime`, `FormAmbiguousValueType`, `FormBareParameterAsType`, `FormCharsetRemoved`, `FormComponentNotCarried`, `FormDuplicateParameterMerged`, `FormExtraComponentsDropped`, `FormFolding`, `FormGroupNotCarried`, `FormInlineBinaryAsDataUri`, `FormInvalidXmlCharacter`, `FormItemOrder`, `FormLineNotCarried`, `FormNameCase`, `FormParameterNotCarried`, `FormPropertyNotCarried`, `FormRedundantValueParameter`, `FormStructuredPadded`, `FormTransferEncodingRemoved`, `FormTypeNameNotCarried`, `FormValueNotRepresentable`, `FormVersionNotCarried`, `FormVersionSpecificForm`, `NotACalendarItem`, `OrganizerNotAnAddress`, `RecurrenceEnd`, `RecurrenceKind`, `RelativeRecurrence`, `Unmapped` | Appointments, tasks and notes (VJOURNAL) carry their fields; the rest is listed as Unmapped. |
| VCard | Lost | None | None | Lost | skipped: `NotContactItem` | Not an item of that family; it is skipped and counted. |
| JCard | Lost | None | None | Lost | skipped: `NotContactItem` | Not an item of that family; it is skipped and counted. |
| XCard | Lost | None | None | Lost | skipped: `NotContactItem` | Not an item of that family; it is skipped and counted. |
| Tnef | Lost | Lost | None | Lost | skipped: `NotMapiConvertible` | An OLM record has no property bag; it is skipped and counted. |

### Outlook for Mac contact or group (OLM)

| To | Content | Properties | Flags | Folders | Codes it can report | Note |
|---|---|---|---|---|---|---|
| Eml | Approximated | Lost | None | Derived | `FlagsNotCarried`, `FolderNameChanged`, `KeywordsNotCarried`, `MemberName`, `NotAContact`, `Unmapped` | Routed to a .vcf file in the side directory (or skipped). |
| Mbox | Approximated | Lost | None | Derived | `FlagsNotCarried`, `FolderNameChanged`, `KeywordsNotCarried`, `MemberName`, `NotAContact`, `Unmapped` | Routed to a .vcf file in the side directory (or skipped). |
| Maildir | Approximated | Lost | None | Derived | `FlagsNotCarried`, `FolderNameChanged`, `KeywordsNotCarried`, `MemberName`, `NotAContact`, `Unmapped` | Routed to a .vcf file in the side directory (or skipped). |
| AppleMail | Approximated | Lost | None | Derived | `FlagsNotCarried`, `FolderNameChanged`, `KeywordsNotCarried`, `MemberName`, `NotAContact`, `Unmapped` | Routed to a .vcf file in the side directory (or skipped). |
| VCard | Approximated | Lost | None | Lost | `MemberName`, `NotAContact`, `Unmapped` | Contacts and groups (KIND:group) carry their fields; the rest is listed as Unmapped. |
| JCard | Approximated | Lost | None | Lost | `FormAmbiguousValueType`, `FormBareParameterAsType`, `FormCharsetRemoved`, `FormComponentNotCarried`, `FormDuplicateParameterMerged`, `FormExtraComponentsDropped`, `FormFolding`, `FormGroupNotCarried`, `FormInlineBinaryAsDataUri`, `FormInvalidXmlCharacter`, `FormItemOrder`, `FormLineNotCarried`, `FormNameCase`, `FormParameterNotCarried`, `FormPropertyNotCarried`, `FormRedundantValueParameter`, `FormStructuredPadded`, `FormTransferEncodingRemoved`, `FormTypeNameNotCarried`, `FormValueNotRepresentable`, `FormVersionNotCarried`, `FormVersionSpecificForm`, `MemberName`, `NotAContact`, `Unmapped` | Contacts and groups (KIND:group) carry their fields; the rest is listed as Unmapped. |
| XCard | Approximated | Lost | None | Lost | `FormAmbiguousValueType`, `FormBareParameterAsType`, `FormCharsetRemoved`, `FormComponentNotCarried`, `FormDuplicateParameterMerged`, `FormExtraComponentsDropped`, `FormFolding`, `FormGroupNotCarried`, `FormInlineBinaryAsDataUri`, `FormInvalidXmlCharacter`, `FormItemOrder`, `FormLineNotCarried`, `FormNameCase`, `FormParameterNotCarried`, `FormPropertyNotCarried`, `FormRedundantValueParameter`, `FormStructuredPadded`, `FormTransferEncodingRemoved`, `FormTypeNameNotCarried`, `FormValueNotRepresentable`, `FormVersionNotCarried`, `FormVersionSpecificForm`, `MemberName`, `NotAContact`, `Unmapped` | Contacts and groups (KIND:group) carry their fields; the rest is listed as Unmapped. |
| Ics | Lost | None | None | Lost | skipped: `NotCalendarItem` | Not an item of that family; it is skipped and counted. |
| JCal | Lost | None | None | Lost | skipped: `NotCalendarItem` | Not an item of that family; it is skipped and counted. |
| XCal | Lost | None | None | Lost | skipped: `NotCalendarItem` | Not an item of that family; it is skipped and counted. |
| Tnef | Lost | Lost | None | Lost | skipped: `NotMapiConvertible` | An OLM record has no property bag; it is skipped and counted. |

### Outlook for Mac categories and other records (OLM)

| To | Content | Properties | Flags | Folders | Codes it can report | Note |
|---|---|---|---|---|---|---|
| Eml | Lost | None | None | Lost | skipped: `NotMail` | A category or settings record is not mail, a calendar item or a contact; it is skipped and counted. |
| Mbox | Lost | None | None | Lost | skipped: `NotMail` | A category or settings record is not mail, a calendar item or a contact; it is skipped and counted. |
| Maildir | Lost | None | None | Lost | skipped: `NotMail` | A category or settings record is not mail, a calendar item or a contact; it is skipped and counted. |
| AppleMail | Lost | None | None | Lost | skipped: `NotMail` | A category or settings record is not mail, a calendar item or a contact; it is skipped and counted. |
| Ics | Lost | None | None | Lost | skipped: `NotCalendarItem` | A category or settings record is not mail, a calendar item or a contact; it is skipped and counted. |
| VCard | Lost | None | None | Lost | skipped: `NotContactItem` | A category or settings record is not mail, a calendar item or a contact; it is skipped and counted. |
| JCal | Lost | None | None | Lost | skipped: `NotCalendarItem` | A category or settings record is not mail, a calendar item or a contact; it is skipped and counted. |
| JCard | Lost | None | None | Lost | skipped: `NotContactItem` | A category or settings record is not mail, a calendar item or a contact; it is skipped and counted. |
| XCal | Lost | None | None | Lost | skipped: `NotCalendarItem` | A category or settings record is not mail, a calendar item or a contact; it is skipped and counted. |
| XCard | Lost | None | None | Lost | skipped: `NotContactItem` | A category or settings record is not mail, a calendar item or a contact; it is skipped and counted. |
| Tnef | Lost | None | None | Lost | skipped: `NotMapiConvertible` | A category or settings record is not mail, a calendar item or a contact; it is skipped and counted. |

### Calendar file (ICS, jCal, xCal)

| To | Content | Properties | Flags | Folders | Codes it can report | Note |
|---|---|---|---|---|---|---|
| Eml | Exact | None | None | Lost | none | Kept as the file's own bytes in the side directory. |
| Mbox | Exact | None | None | Lost | none | Kept as the file's own bytes in the side directory. |
| Maildir | Exact | None | None | Lost | none | Kept as the file's own bytes in the side directory. |
| AppleMail | Exact | None | None | Lost | none | Kept as the file's own bytes in the side directory. |
| Ics | Exact | None | None | Lost | none | An iCalendar file converted to iCalendar is the same bytes; the forms of RFC 7265 and RFC 6321 carry the same components, and every difference is a Form code. |
| JCal | Derived | None | None | Lost | `FormAmbiguousValueType`, `FormBareParameterAsType`, `FormCharsetRemoved`, `FormComponentNotCarried`, `FormDuplicateParameterMerged`, `FormExtraComponentsDropped`, `FormFolding`, `FormGroupNotCarried`, `FormInlineBinaryAsDataUri`, `FormInvalidXmlCharacter`, `FormItemOrder`, `FormLineNotCarried`, `FormNameCase`, `FormParameterNotCarried`, `FormPropertyNotCarried`, `FormRedundantValueParameter`, `FormStructuredPadded`, `FormTransferEncodingRemoved`, `FormTypeNameNotCarried`, `FormValueNotRepresentable`, `FormVersionNotCarried`, `FormVersionSpecificForm` | Between forms the data carries; folding and name case change the text and are reported as bookkeeping. |
| XCal | Derived | None | None | Lost | `FormAmbiguousValueType`, `FormBareParameterAsType`, `FormCharsetRemoved`, `FormComponentNotCarried`, `FormDuplicateParameterMerged`, `FormExtraComponentsDropped`, `FormFolding`, `FormGroupNotCarried`, `FormInlineBinaryAsDataUri`, `FormInvalidXmlCharacter`, `FormItemOrder`, `FormLineNotCarried`, `FormNameCase`, `FormParameterNotCarried`, `FormPropertyNotCarried`, `FormRedundantValueParameter`, `FormStructuredPadded`, `FormTransferEncodingRemoved`, `FormTypeNameNotCarried`, `FormValueNotRepresentable`, `FormVersionNotCarried`, `FormVersionSpecificForm` | Between forms the data carries; folding and name case change the text and are reported as bookkeeping. |
| VCard | Lost | None | None | Lost | skipped: `NotContactItem` | Not an item of that family; it is skipped and counted. |
| JCard | Lost | None | None | Lost | skipped: `NotContactItem` | Not an item of that family; it is skipped and counted. |
| XCard | Lost | None | None | Lost | skipped: `NotContactItem` | Not an item of that family; it is skipped and counted. |
| Tnef | Lost | None | None | Lost | skipped: `NotMail` | A calendar file is not a message; it is skipped and counted. |

### Card file (vCard, jCard, xCard)

| To | Content | Properties | Flags | Folders | Codes it can report | Note |
|---|---|---|---|---|---|---|
| Eml | Exact | None | None | Lost | none | Kept as the file's own bytes in the side directory. |
| Mbox | Exact | None | None | Lost | none | Kept as the file's own bytes in the side directory. |
| Maildir | Exact | None | None | Lost | none | Kept as the file's own bytes in the side directory. |
| AppleMail | Exact | None | None | Lost | none | Kept as the file's own bytes in the side directory. |
| VCard | Exact | None | None | Lost | none | A vCard file converted to vCard is the same bytes. |
| JCard | Derived | None | None | Lost | `FormAmbiguousValueType`, `FormBareParameterAsType`, `FormCharsetRemoved`, `FormComponentNotCarried`, `FormDuplicateParameterMerged`, `FormExtraComponentsDropped`, `FormFolding`, `FormGroupNotCarried`, `FormInlineBinaryAsDataUri`, `FormInvalidXmlCharacter`, `FormItemOrder`, `FormLineNotCarried`, `FormNameCase`, `FormParameterNotCarried`, `FormPropertyNotCarried`, `FormRedundantValueParameter`, `FormStructuredPadded`, `FormTransferEncodingRemoved`, `FormTypeNameNotCarried`, `FormValueNotRepresentable`, `FormVersionNotCarried`, `FormVersionSpecificForm` | Between forms the data carries; every difference is a Form code. |
| XCard | Derived | None | None | Lost | `FormAmbiguousValueType`, `FormBareParameterAsType`, `FormCharsetRemoved`, `FormComponentNotCarried`, `FormDuplicateParameterMerged`, `FormExtraComponentsDropped`, `FormFolding`, `FormGroupNotCarried`, `FormInlineBinaryAsDataUri`, `FormInvalidXmlCharacter`, `FormItemOrder`, `FormLineNotCarried`, `FormNameCase`, `FormParameterNotCarried`, `FormPropertyNotCarried`, `FormRedundantValueParameter`, `FormStructuredPadded`, `FormTransferEncodingRemoved`, `FormTypeNameNotCarried`, `FormValueNotRepresentable`, `FormVersionNotCarried`, `FormVersionSpecificForm` | Between forms the data carries; every difference is a Form code. |
| Ics | Lost | None | None | Lost | skipped: `NotCalendarItem` | Not an item of that family; it is skipped and counted. |
| JCal | Lost | None | None | Lost | skipped: `NotCalendarItem` | Not an item of that family; it is skipped and counted. |
| XCal | Lost | None | None | Lost | skipped: `NotCalendarItem` | Not an item of that family; it is skipped and counted. |
| Tnef | Lost | None | None | Lost | skipped: `NotMail` | A card file is not a message; it is skipped and counted. |

<!-- matrix:end -->

## Codes added by the store layer

| Code | Kind | Meaning |
|---|---|---|
| `FolderNameChanged` | Approximated | a character a file system or Maildir++ cannot hold in a folder name was replaced |
| `FolderFlattened` | Dropped | a single mbox file has no folders |
| `FlagsNotCarried` | Dropped | the destination keeps no flags, or an mbox message's own Status fields were kept over the source's flags |
| `KeywordsNotCarried` | Dropped | the destination keeps no keywords (or a Maildir folder is past 26) |
| `FlagsWrittenAsHeaders` | Internal | mbox: Status, X-Status and X-Keywords fields were added |
| `FinalLineEndingAdded` | Approximated | mbox: the message did not end in a line ending and gained one |
| `EnvelopeDateUnknown` | Approximated | mbox: the message has no date; the envelope line carries the Unix epoch so output is reproducible |
| `OlmFieldDiffersFromSource` | Overridden | an OLM record holds the original message as well; where a field of the record differs from it, the original is written and the field named |
| `BccNotWritten` | Dropped | a message rebuilt from the shared view has blind-copy recipients; they are not written |
| `DateZoneNotStored` | Approximated | a message rebuilt from the shared view is dated in UTC |
| `EmbeddedMessageAsData` | Approximated | a message rebuilt from the shared view writes an embedded message as an ordinary attachment |
| `FormFolding`, `FormNameCase`, `FormItemOrder`, `FormLineNotCarried`, `FormRedundantValueParameter`, `FormDuplicateParameterMerged`, `FormCharsetRemoved` | Internal or Approximated | what the JSON or XML form of a calendar or card could not carry exactly (the calendar library's form losses by kind); folding and name case are bookkeeping |
