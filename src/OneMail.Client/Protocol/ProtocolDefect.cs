namespace RecursiveAI.OneMail.Client.Protocol;

/// <summary>What a protocol reader repaired, guessed or gave up on. Append new values at the end.</summary>
public enum ProtocolDefectKind
{
    /// <summary>The line does not follow the grammar; the response carries what was understood and the raw line.</summary>
    Malformed = 0,

    /// <summary>A parenthesised list was not closed before the end of the line.</summary>
    UnterminatedList,

    /// <summary>A quoted string was not closed before the end of the line.</summary>
    UnterminatedQuoted,

    /// <summary>A literal announcement (<c>{n}</c>) has a bad size.</summary>
    BadLiteralSize,

    /// <summary>A literal was larger than the buffering limit and nobody streamed it; its bytes were skipped.</summary>
    LiteralSkipped,

    /// <summary>The nesting of lists went deeper than the limit; the rest of the line was kept raw.</summary>
    NestingTooDeep,

    /// <summary>An SMTP reply line had a different code from the first line of the same reply.</summary>
    ReplyCodeChanged,

    /// <summary>An SMTP reply line had no three-digit code; it was taken as text of the final line.</summary>
    ReplyLineWithoutCode,

    /// <summary>A POP3 status line started with neither <c>+OK</c> nor <c>-ERR</c>.</summary>
    UnknownStatus,

    /// <summary>A bare line feed ended a line instead of CRLF.</summary>
    BareLineFeed,

    /// <summary>A value that should be a number was not (or overflowed).</summary>
    BadNumber,

    /// <summary>An item the typed view expects was missing or had the wrong shape; the raw tree is intact.</summary>
    UnexpectedShape,
}

/// <summary>One repair a protocol reader made, with where it happened.</summary>
/// <param name="Kind">What was repaired.</param>
/// <param name="Offset">Byte offset in the line (or logical response text) where it was noticed.</param>
/// <param name="Message">A short description for logs.</param>
public readonly record struct ProtocolDefect(ProtocolDefectKind Kind, int Offset, string Message);
