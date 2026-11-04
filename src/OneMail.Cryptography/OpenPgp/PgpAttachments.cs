using RecursiveAI.OneCrypto.OpenPgp;
using RecursiveAI.OneMail.Mime;

namespace RecursiveAI.OneMail.Cryptography.OpenPgp;

/// <summary>A public key block attached to a message.</summary>
public sealed class PgpAttachedKey
{
    internal PgpAttachedKey(string path, string? fileName, IReadOnlyList<PgpCertificate> certificates, bool fromProtectedContent)
    {
        Path = path;
        FileName = fileName;
        Certificates = certificates;
        FromProtectedContent = fromProtectedContent;
    }

    /// <summary>Where the part sits: <c>1</c>, <c>2.1</c> … in the outer message, or the same kind of path inside the protected content prefixed with <c>protected:</c>.</summary>
    public string Path { get; }

    /// <summary>The attachment's file name, if it has one.</summary>
    public string? FileName { get; }

    /// <summary>The public certificates the part holds; empty when it holds none that parse. Secret key material is never accepted.</summary>
    public IReadOnlyList<PgpCertificate> Certificates { get; }

    /// <summary>True when the part was inside the decrypted or verified content. Whether that content was authenticated is <see cref="PgpResult.IsAuthenticated"/>; either way the key is the sender's claim, not evidence.</summary>
    public bool FromProtectedContent { get; }
}

/// <summary>An attachment that holds OpenPGP data.</summary>
public sealed class PgpAttachmentRef
{
    internal PgpAttachmentRef(string path, MimePart part, string? fileName)
    {
        Path = path;
        Part = part;
        FileName = fileName;
    }

    /// <summary>Where the part sits (see <see cref="PgpAttachedKey.Path"/>).</summary>
    public string Path { get; }

    /// <summary>The part; pass it to <see cref="PgpMimeProcessor.OpenAttachment"/>.</summary>
    public MimePart Part { get; }

    /// <summary>The attachment's file name, if any.</summary>
    public string? FileName { get; }
}

/// <summary>The outcome of <see cref="PgpMimeProcessor.OpenAttachment"/>.</summary>
public sealed class PgpAttachmentResult
{
    internal PgpAttachmentResult(PgpLayer layer, ReadOnlyMemory<byte> data, string? fileName)
    {
        Layer = layer;
        Data = data;
        FileName = fileName;
    }

    /// <summary>The layer: status, signers found inside the encrypted data, protection, issues. Kind is <see cref="PgpLayerKind.Encrypted"/> for encrypted data and <see cref="PgpLayerKind.InlineMessage"/> otherwise.</summary>
    public PgpLayer Layer { get; }

    /// <summary>The plaintext; empty when the layer failed.</summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>The attachment's name without a <c>.pgp</c>, <c>.gpg</c> or <c>.asc</c> suffix; null when it had no name.</summary>
    public string? FileName { get; }

    /// <summary>True when the data was decrypted (or its signature checked) and every signature inside is valid.</summary>
    public bool Succeeded => Layer.Succeeded;

    /// <summary>True when <see cref="Succeeded"/> and at least one signature is valid. Decryption alone never authenticates.</summary>
    public bool IsAuthenticated => Layer.Succeeded && Layer.Signers.Any(static s => s.IsValid);
}
