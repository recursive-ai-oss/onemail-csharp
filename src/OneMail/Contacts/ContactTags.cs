namespace RecursiveAI.OneMail.Contacts;

/// <summary>Property IDs the contact view and the vCard conversions use that the generated table does not carry (MS-OXPROPS).</summary>
internal static class ContactTags
{
    public const ushort Generation = 0x3A05;
    public const ushort UserX509Certificate = 0x3A70;
    public const ushort AttachmentContactPhoto = 0x7FFF;
    public const ushort Sensitivity = 0x0036;
    public const ushort LastModificationTime = 0x3008;
    public const ushort NormalizedSubject = 0x0E1D;
    public const ushort ConversationTopic = 0x0070;
    public const ushort AttachExtension = 0x3703;

    // MS-OXPROPS 2.x property tags for telephone numbers, in the order ContactPhoneKind lists them.
    public static readonly (ContactPhoneKind Kind, ushort Id)[] PhoneTags =
    [
        (ContactPhoneKind.Business, 0x3A08),
        (ContactPhoneKind.Business2, 0x3A1B),
        (ContactPhoneKind.Home, 0x3A09),
        (ContactPhoneKind.Home2, 0x3A2F),
        (ContactPhoneKind.Mobile, 0x3A1C),
        (ContactPhoneKind.Car, 0x3A1E),
        (ContactPhoneKind.Pager, 0x3A21),
        (ContactPhoneKind.Radio, 0x3A1D),
        (ContactPhoneKind.Primary, 0x3A1A),
        (ContactPhoneKind.Other, 0x3A1F),
        (ContactPhoneKind.Company, 0x3A57),
        (ContactPhoneKind.Assistant, 0x3A2E),
        (ContactPhoneKind.Callback, 0x3A02),
        (ContactPhoneKind.Isdn, 0x3A2D),
        (ContactPhoneKind.Telex, 0x3A2C),
        (ContactPhoneKind.TtyTdd, 0x3A4B),
        (ContactPhoneKind.BusinessFax, 0x3A24),
        (ContactPhoneKind.HomeFax, 0x3A25),
        (ContactPhoneKind.PrimaryFax, 0x3A23),
    ];
}
