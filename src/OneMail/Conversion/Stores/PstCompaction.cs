using RecursiveAI.OneMail.Pst.Messaging;

namespace RecursiveAI.OneMail.Conversion.Stores;

/// <summary>
/// Compaction of a PST: the live store is read once and streamed through the PST builder into a new file, so the new file has no free space and none of
/// the old file's leftovers. It is a rewrite, never an in-place operation, so the source is not touched.
/// </summary>
public static class PstCompaction
{
    /// <summary>Writes the items of a Unicode, ANSI or offline PST into a new Unicode PST, each in the folder it came from, with every property it has.</summary>
    /// <param name="source">The PST to read.</param>
    /// <param name="destination">The new file; it must not exist.</param>
    /// <param name="options">Conversion options, or null for the defaults.</param>
    /// <param name="progress">Called after each item.</param>
    /// <param name="cancellationToken">Cancels the rewrite; the partial destination is deleted.</param>
    /// <returns>What was written and what did not carry over (the folders' own properties other than name and class are not carried).</returns>
    /// <exception cref="ArgumentException">The source is not a PST.</exception>
    /// <exception cref="IOException">The destination exists or cannot be written.</exception>
    public static Task<StoreConversionReport> CompactAsync(string source, string destination, StoreConversionOptions? options = null, IProgress<StoreItemResult>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(destination);
        if (!PstStore.TryOpen(source, out PstStore? probe, out _))
        {
            throw new ArgumentException("The source is not a PST file.", nameof(source));
        }

        probe!.Dispose();
        return new StoreConverter(options).ConvertAsync(source, ConvertFormat.Pst, destination, progress, cancellationToken);
    }
}
