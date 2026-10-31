using System.Net;
using System.Text;
using RecursiveAI.OneMail.Mime.Reading;
using RecursiveAI.OneMail.Mime.Reports;

namespace RecursiveAI.OneMail.Mime.Authentication;

/// <summary>The result keyword of an authentication method (RFC 8601 §2.7, with the SPF results of RFC 7208 §2.6).</summary>
public enum AuthenticationResultKind
{
    /// <summary>A keyword not listed here. The text is in <see cref="AuthenticationMethodResult.Result"/>.</summary>
    Other = 0,

    /// <summary>The check passed.</summary>
    Pass,

    /// <summary>The check failed.</summary>
    Fail,

    /// <summary>SPF: the domain does not assert the host is authorised, but does not say it is not (<c>softfail</c>).</summary>
    SoftFail,

    /// <summary>The domain makes no assertion.</summary>
    Neutral,

    /// <summary>The message was not signed, or the domain publishes no policy.</summary>
    None,

    /// <summary>A local policy decided the result.</summary>
    Policy,

    /// <summary>A transient error, for example a DNS timeout.</summary>
    TempError,

    /// <summary>A permanent error, for example a malformed record.</summary>
    PermError,
}

/// <summary>One property of a method result, <c>ptype.property=value</c>, for example <c>smtp.mailfrom=a@example.com</c> or <c>header.d=example.com</c> (RFC 8601 §2.2).</summary>
public sealed class AuthenticationProperty
{
    internal AuthenticationProperty(string propertyType, string name, string value, string? comment)
    {
        PropertyType = propertyType;
        Name = name;
        Value = value;
        Comment = comment;
    }

    /// <summary>The property type in lower case: <c>smtp</c>, <c>header</c>, <c>body</c>, <c>policy</c>, or empty when the property was written without one (see <see cref="MimeDefectKind.AmbiguousAuthenticationResultsProperty"/>).</summary>
    public string PropertyType { get; }

    /// <summary>The property name in lower case, for example <c>mailfrom</c>, <c>d</c>, <c>b</c>.</summary>
    public string Name { get; }

    /// <summary>The value, with quotes and escapes removed. Empty when the value was written empty.</summary>
    public string Value { get; }

    /// <summary>The comment that followed the property, without parentheses, or null.</summary>
    public string? Comment { get; }

    /// <inheritdoc/>
    public override string ToString() => (PropertyType.Length > 0 ? PropertyType + "." : string.Empty) + Name + "=" + Value;
}

/// <summary>One <c>method=result</c> clause of an <c>Authentication-Results</c> field with its reason and properties (RFC 8601 §2.2).</summary>
public sealed class AuthenticationMethodResult
{
    internal AuthenticationMethodResult(string method, int? methodVersion, string result, string? resultComment)
    {
        Method = method;
        MethodVersion = methodVersion;
        Result = result;
        ResultComment = resultComment;
        Kind = result switch
        {
            "pass" => AuthenticationResultKind.Pass,
            "fail" or "hardfail" => AuthenticationResultKind.Fail,
            "softfail" => AuthenticationResultKind.SoftFail,
            "neutral" => AuthenticationResultKind.Neutral,
            "none" => AuthenticationResultKind.None,
            "policy" => AuthenticationResultKind.Policy,
            "temperror" => AuthenticationResultKind.TempError,
            "permerror" => AuthenticationResultKind.PermError,
            _ => AuthenticationResultKind.Other,
        };
    }

    /// <summary>The method in lower case: <c>dkim</c>, <c>spf</c>, <c>dmarc</c>, <c>arc</c>, <c>iprev</c>, <c>auth</c> or another registered name.</summary>
    public string Method { get; }

    /// <summary>The method version when written as <c>method/version</c>, else null.</summary>
    public int? MethodVersion { get; }

    /// <summary>The result keyword in lower case, as written.</summary>
    public string Result { get; }

    /// <summary>The result as a keyword; <see cref="AuthenticationResultKind.Other"/> for one this enum does not list.</summary>
    public AuthenticationResultKind Kind { get; }

    /// <summary>The first comment after the result, without parentheses (for example <c>good signature</c>), or null.</summary>
    public string? ResultComment { get; }

    /// <summary>The <c>reason=</c> text, or null.</summary>
    public string? Reason { get; internal set; }

    /// <summary>The properties in order.</summary>
    public IReadOnlyList<AuthenticationProperty> Properties => PropertyList;

    internal List<AuthenticationProperty> PropertyList { get; } = [];

    /// <summary>The value of the first property with the given type and name (ASCII case-insensitive), or null.</summary>
    /// <param name="propertyType">For example <c>header</c>.</param>
    /// <param name="name">For example <c>d</c>.</param>
    /// <returns>The value, or null when there is no such property.</returns>
    public string? GetProperty(string propertyType, string name)
    {
        ArgumentNullException.ThrowIfNull(propertyType);
        ArgumentNullException.ThrowIfNull(name);
        foreach (AuthenticationProperty p in PropertyList)
        {
            if (string.Equals(p.PropertyType, propertyType, StringComparison.OrdinalIgnoreCase) && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return p.Value;
            }
        }

        return null;
    }
}

/// <summary>
/// A parsed <c>Authentication-Results</c> field (RFC 8601). Parsing only: nothing is verified, and a result says what the
/// receiving server claims. Lenient: whatever can be read is returned, and what was skipped is a defect with its offset.
/// </summary>
public sealed class AuthenticationResults
{
    private readonly List<MimeDefect> _defects = [];

    private AuthenticationResults(string authServId, int? version)
    {
        AuthServId = authServId;
        Version = version;
    }

    /// <summary>The authentication service identifier: the host name of the server that checked, as written. Empty when the field lacks one.</summary>
    public string AuthServId { get; private set; }

    /// <summary>The version number written after the identifier, or null when absent (which RFC 8601 §2.2 reads as 1).</summary>
    public int? Version { get; private set; }

    /// <summary>True for the form <c>authserv-id; none</c>: the server made no checks.</summary>
    public bool IsNone { get; private set; }

    /// <summary>The method results in order.</summary>
    public IReadOnlyList<AuthenticationMethodResult> Results => ResultList;

    internal List<AuthenticationMethodResult> ResultList { get; } = [];

    /// <summary>What was skipped or guessed. Offsets are in the header value plus the base offset given to <see cref="Parse(ReadOnlySpan{byte}, long, int)"/>.</summary>
    public IReadOnlyList<MimeDefect> Defects => _defects;

    /// <summary>Parses text.</summary>
    /// <param name="value">The field value (after the colon). A header with folds and comments is fine.</param>
    /// <returns>The result; never null.</returns>
    public static AuthenticationResults Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Parse(Encoding.UTF8.GetBytes(value));
    }

    /// <summary>Parses a field value. Never throws on malformed content.</summary>
    /// <param name="value">The raw value after the colon.</param>
    /// <param name="baseOffset">Added to defect offsets.</param>
    /// <param name="depth">The entity depth recorded in defects.</param>
    /// <returns>The result; never null.</returns>
    public static AuthenticationResults Parse(ReadOnlySpan<byte> value, long baseOffset = 0, int depth = 0)
    {
        var sc = new Scanner(value, baseOffset, depth);
        var r = new AuthenticationResults(string.Empty, null);
        sc.Run(r);
        r._defects.AddRange(sc.Defects);
        return r;
    }

    /// <summary>Parses a field value and tells whether it had an authentication service identifier.</summary>
    /// <param name="value">The raw value after the colon.</param>
    /// <param name="result">The result.</param>
    /// <returns>True when an identifier was found.</returns>
    public static bool TryParse(ReadOnlySpan<byte> value, out AuthenticationResults result)
    {
        result = Parse(value);
        return result.AuthServId.Length > 0;
    }

    // RFC 8601 §2.2:
    //   authres-header-field = authserv-id [ CFWS authres-version ] ( [CFWS] ";" [CFWS] "none" / 1*resinfo )
    //   resinfo = [CFWS] ";" methodspec [ CFWS reasonspec ] *( CFWS propspec )
    private sealed class Scanner
    {
        private readonly byte[] _v;
        private readonly long _base;
        private readonly int _depth;
        private int _i;

        public Scanner(ReadOnlySpan<byte> v, long baseOffset, int depth)
        {
            _v = v.ToArray();
            _base = baseOffset;
            _depth = depth;
        }

        public List<MimeDefect> Defects { get; } = [];

        private void D(MimeDefectKind k, int at, int len) => Defects.Add(new MimeDefect(k, _base + at, len, _depth));

        public void Run(AuthenticationResults r)
        {
            // authserv-id
            Cfws(out _);
            bool implicitFirst = false;
            if (_i >= _v.Length || _v[_i] == (byte)';')
            {
                D(MimeDefectKind.AuthenticationResultsMissingId, _i, 0);
            }
            else if (StartsWithMethodSpec())
            {
                // Exchange-style fields begin with the first result and have no identifier (and no semicolon before it).
                D(MimeDefectKind.AuthenticationResultsMissingId, _i, 0);
                implicitFirst = true;
            }
            else
            {
                r.AuthServId = ReadValue();
                // version: digits only, up to the next separator
                Cfws(out _);
                if (_i < _v.Length && _v[_i] != (byte)';')
                {
                    int s = _i;
                    string tok = ReadRun(stopAtEquals: false);
                    if (tok.Length > 0 && tok.All(char.IsAsciiDigit) && tok.Length <= 9)
                    {
                        r.Version = int.Parse(tok, System.Globalization.CultureInfo.InvariantCulture);
                        if (r.Version != 1)
                        {
                            D(MimeDefectKind.AuthenticationResultsInvalidVersion, s, tok.Length);
                        }
                    }
                    else
                    {
                        D(MimeDefectKind.AuthenticationResultsInvalidVersion, s, Math.Max(1, _i - s));
                        SkipToSemicolon();
                    }
                }
            }

            bool first = true;
            while (true)
            {
                Cfws(out _);
                if (_i >= _v.Length)
                {
                    break;
                }

                if (!implicitFirst)
                {
                    if (_v[_i] != (byte)';')
                    {
                        D(MimeDefectKind.InvalidAuthenticationResults, _i, 0);
                        SkipToSemicolon();
                        continue;
                    }

                    int semi = _i;
                    _i++;
                    Cfws(out _);
                    if (_i >= _v.Length)
                    {
                        break; // a trailing semicolon ends the field
                    }

                    if (_v[_i] == (byte)';')
                    {
                        D(MimeDefectKind.InvalidAuthenticationResults, semi, 1); // an empty clause
                        continue;
                    }
                }

                implicitFirst = false;

                int start = _i;
                if (IsNoneClause())
                {
                    _i += 4;
                    if (first)
                    {
                        r.IsNone = true;
                    }
                    else
                    {
                        D(MimeDefectKind.AmbiguousAuthenticationResultsNone, start, 4);
                    }

                    first = false;
                    continue;
                }

                if (r.IsNone)
                {
                    D(MimeDefectKind.AmbiguousAuthenticationResultsNone, start, 0);
                }

                first = false;
                AuthenticationMethodResult? m = ReadMethodSpec();
                if (m is null)
                {
                    D(MimeDefectKind.InvalidAuthenticationResults, start, Math.Max(1, _i - start));
                    SkipToSemicolon();
                    continue;
                }

                r.ResultList.Add(m);
                ReadProps(m);
            }
        }

        // True when the text at the cursor is "method[/version] = ": a result clause, not an identifier.
        private bool StartsWithMethodSpec()
        {
            int save = _i;
            bool ok = false;
            if (ReadKeyword().Length > 0)
            {
                Cfws(out _);
                if (_i < _v.Length && _v[_i] == (byte)'/')
                {
                    _i++;
                    Cfws(out _);
                    while (_i < _v.Length && char.IsAsciiDigit((char)_v[_i]))
                    {
                        _i++;
                    }

                    Cfws(out _);
                }

                ok = _i < _v.Length && _v[_i] == (byte)'=';
            }

            _i = save;
            return ok;
        }

        private bool IsNoneClause()
        {
            if (_i + 4 > _v.Length || !StructuredHeader.EqualsIgnoreCase(_v.AsSpan(_i, 4), "none"u8))
            {
                return false;
            }

            int j = _i + 4;
            Cfws(ref j, out _);
            return j >= _v.Length || _v[j] == (byte)';';
        }

        private AuthenticationMethodResult? ReadMethodSpec()
        {
            string method = ReadKeyword();
            if (method.Length == 0)
            {
                return null;
            }

            int? version = null;
            Cfws(out _);
            if (_i < _v.Length && _v[_i] == (byte)'/')
            {
                _i++;
                Cfws(out _);
                int s = _i;
                while (_i < _v.Length && char.IsAsciiDigit((char)_v[_i]))
                {
                    _i++;
                }

                if (_i == s || _i - s > 9)
                {
                    return null;
                }

                version = int.Parse(Encoding.ASCII.GetString(_v, s, _i - s), System.Globalization.CultureInfo.InvariantCulture);
                Cfws(out _);
            }

            if (_i >= _v.Length || _v[_i] != (byte)'=')
            {
                return null;
            }

            _i++;
            Cfws(out _);
            string result = ReadKeyword();
            if (result.Length == 0)
            {
                return null;
            }

            Cfws(out string? comment);
            return new AuthenticationMethodResult(method.ToLowerInvariant(), version, result.ToLowerInvariant(), comment);
        }

        private void ReadProps(AuthenticationMethodResult m)
        {
            while (true)
            {
                Cfws(out string? _);
                if (_i >= _v.Length || _v[_i] == (byte)';')
                {
                    return;
                }

                int start = _i;
                string name = ReadRun(stopAtEquals: true);
                Cfws(out _);

                // RFC 8601 §2.2: ptype [CFWS] "." [CFWS] property, so the dot may stand apart with comments around it.
                if (name.Length > 0 && (name[^1] == '.' || (_i < _v.Length && _v[_i] == (byte)'.')))
                {
                    if (name[^1] != '.')
                    {
                        _i++;
                        name += ".";
                    }

                    Cfws(out _);
                    name += ReadRun(stopAtEquals: true);
                    Cfws(out _);
                }

                if (name.Length == 0 || _i >= _v.Length || _v[_i] != (byte)'=')
                {
                    // a word without a value
                    D(MimeDefectKind.InvalidAuthenticationResults, start, Math.Max(1, _i - start));
                    if (name.Length == 0 && _i < _v.Length && _v[_i] != (byte)';')
                    {
                        _i++;
                    }

                    continue;
                }

                _i++;
                Cfws(out _);
                string value = _i < _v.Length && _v[_i] == (byte)'"' ? ReadQuoted() : ReadRun(stopAtEquals: false);
                Cfws(out string? comment);
                if (string.Equals(name, "reason", StringComparison.OrdinalIgnoreCase))
                {
                    m.Reason = value;
                    continue;
                }

                int dot = name.IndexOf('.', StringComparison.Ordinal);
                if (dot > 0 && dot < name.Length - 1)
                {
                    m.PropertyList.Add(new AuthenticationProperty(name[..dot].ToLowerInvariant(), name[(dot + 1)..].ToLowerInvariant(), value, comment));
                }
                else
                {
                    // Many servers write vendor properties without a ptype (RFC 8601 §2.2 requires one). Clients keep them; so do we.
                    D(MimeDefectKind.AmbiguousAuthenticationResultsProperty, start, name.Length);
                    m.PropertyList.Add(new AuthenticationProperty(string.Empty, name.ToLowerInvariant(), value, comment));
                }
            }
        }

        // The authserv-id is a "value" (RFC 8601 §2.2): a token or a quoted-string.
        private string ReadValue() => _i < _v.Length && _v[_i] == (byte)'"' ? ReadQuoted() : ReadRun(stopAtEquals: false);

        // A Keyword is letters, digits and hyphens; method names also hold dots and underscores in vendor extensions.
        private string ReadKeyword()
        {
            int s = _i;
            while (_i < _v.Length && (char.IsAsciiLetterOrDigit((char)_v[_i]) || _v[_i] is (byte)'-' or (byte)'_' or (byte)'.'))
            {
                _i++;
            }

            return Encoding.ASCII.GetString(_v, s, _i - s);
        }

        // A run of bytes up to white space, a semicolon or a comment (and optionally an equals sign).
        private string ReadRun(bool stopAtEquals)
        {
            int s = _i;
            while (_i < _v.Length)
            {
                byte c = _v[_i];
                if (c is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)';' or (byte)'(' || (stopAtEquals && c == (byte)'='))
                {
                    break;
                }

                _i++;
            }

            return ReportText.Decode(_v.AsSpan(s, _i - s));
        }

        private string ReadQuoted()
        {
            var bytes = new List<byte>();
            _i++; // opening quote
            while (_i < _v.Length && _v[_i] != (byte)'"')
            {
                byte c = _v[_i++];
                if (c == (byte)'\\' && _i < _v.Length)
                {
                    c = _v[_i++];
                }
                else if (c is (byte)'\r' or (byte)'\n')
                {
                    continue;
                }

                bytes.Add(c);
            }

            if (_i < _v.Length)
            {
                _i++;
            }
            else
            {
                D(MimeDefectKind.UnterminatedQuote, _i, 0);
            }

            return ReportText.Decode([.. bytes]);
        }

        private void SkipToSemicolon()
        {
            while (_i < _v.Length && _v[_i] != (byte)';')
            {
                if (_v[_i] == (byte)'(')
                {
                    Cfws(out _);
                }
                else if (_v[_i] == (byte)'"')
                {
                    _ = ReadQuoted();
                }
                else
                {
                    _i++;
                }
            }
        }

        private void Cfws(out string? firstComment) => Cfws(ref _i, out firstComment);

        // RFC 5322 §3.2.2: white space, folds and nested comments. The first comment's text is returned.
        private void Cfws(ref int i, out string? firstComment)
        {
            firstComment = null;
            while (i < _v.Length)
            {
                byte c = _v[i];
                if (c is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
                {
                    i++;
                }
                else if (c == (byte)'(')
                {
                    var text = new List<byte>();
                    int depth = 0;
                    while (i < _v.Length)
                    {
                        byte x = _v[i++];
                        if (x == (byte)'\\' && i < _v.Length)
                        {
                            text.Add(_v[i++]);
                        }
                        else if (x == (byte)'(')
                        {
                            if (depth++ > 0)
                            {
                                text.Add(x);
                            }
                        }
                        else if (x == (byte)')')
                        {
                            if (--depth == 0)
                            {
                                break;
                            }

                            text.Add(x);
                        }
                        else if (x is not ((byte)'\r' or (byte)'\n'))
                        {
                            text.Add(x);
                        }
                    }

                    if (depth > 0)
                    {
                        D(MimeDefectKind.InvalidAuthenticationResults, i, 0);
                    }

                    firstComment ??= ReportText.Decode([.. text]).Trim();
                }
                else
                {
                    break;
                }
            }
        }
    }
}

/// <summary>The result keyword of a <c>Received-SPF</c> field (RFC 7208 §9.1, §2.6).</summary>
public enum SpfResult
{
    /// <summary>Not one of the seven keywords.</summary>
    Unknown = 0,

    /// <summary>The domain has no SPF record, or no domain could be checked.</summary>
    None,

    /// <summary>The domain makes no assertion about the host.</summary>
    Neutral,

    /// <summary>The host is authorised.</summary>
    Pass,

    /// <summary>The host is not authorised.</summary>
    Fail,

    /// <summary>The host is probably not authorised.</summary>
    SoftFail,

    /// <summary>A transient error while checking.</summary>
    TempError,

    /// <summary>A permanent error, for example a malformed record.</summary>
    PermError,
}

/// <summary>A parsed <c>Received-SPF</c> field (RFC 7208 §9.1): the result, a comment, and <c>key=value</c> pairs. Parsing only: SPF is not evaluated.</summary>
public sealed class ReceivedSpf
{
    private readonly List<MimeDefect> _defects = [];
    private readonly List<KeyValuePair<string, string>> _pairs = [];

    private ReceivedSpf()
    {
    }

    /// <summary>The result keyword.</summary>
    public SpfResult Result { get; private set; }

    /// <summary>The result as written, lower case.</summary>
    public string ResultText { get; private set; } = string.Empty;

    /// <summary>The first comment after the result (the human-readable explanation), without parentheses, or null.</summary>
    public string? Comment { get; private set; }

    /// <summary>The <c>key=value</c> pairs in order; keys are lower case, values have quotes removed.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Pairs => _pairs;

    /// <summary>What was skipped or guessed. Offsets are in the header value plus the base offset.</summary>
    public IReadOnlyList<MimeDefect> Defects => _defects;

    /// <summary>The value of the first pair called <paramref name="key"/> (ASCII case-insensitive), or null.</summary>
    /// <param name="key">For example <c>client-ip</c>.</param>
    /// <returns>The value or null.</returns>
    public string? Get(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        foreach (KeyValuePair<string, string> p in _pairs)
        {
            if (string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return p.Value;
            }
        }

        return null;
    }

    /// <summary><c>client-ip</c>: the address of the host that sent the message, or null when absent or not an address.</summary>
    public IPAddress? ClientIp => Get("client-ip") is { } s && IPAddress.TryParse(s, out IPAddress? ip) ? ip : null;

    /// <summary><c>envelope-from</c>, or null. Written as found, including angle brackets that some servers add.</summary>
    public string? EnvelopeFrom => Get("envelope-from");

    /// <summary><c>helo</c>, or null.</summary>
    public string? Helo => Get("helo");

    /// <summary><c>identity</c> (<c>mailfrom</c> or <c>helo</c>), or null.</summary>
    public string? Identity => Get("identity");

    /// <summary><c>receiver</c>, or null.</summary>
    public string? Receiver => Get("receiver");

    /// <summary><c>mechanism</c>, or null.</summary>
    public string? Mechanism => Get("mechanism");

    /// <summary><c>problem</c>, or null.</summary>
    public string? Problem => Get("problem");

    /// <summary>Parses text.</summary>
    /// <param name="value">The field value (after the colon).</param>
    /// <returns>The result; never null.</returns>
    public static ReceivedSpf Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Parse(Encoding.UTF8.GetBytes(value));
    }

    /// <summary>Parses a field value. Never throws on malformed content.</summary>
    /// <param name="value">The raw value after the colon.</param>
    /// <param name="baseOffset">Added to defect offsets.</param>
    /// <param name="depth">The entity depth recorded in defects.</param>
    /// <returns>The result; never null.</returns>
    public static ReceivedSpf Parse(ReadOnlySpan<byte> value, long baseOffset = 0, int depth = 0)
    {
        byte[] v = value.ToArray();
        var r = new ReceivedSpf();
        int i = 0;
        void D(int at, int len) => r._defects.Add(new MimeDefect(MimeDefectKind.InvalidReceivedSpf, baseOffset + at, len, depth));

        // header-field = "Received-SPF:" [CFWS] result FWS [comment FWS] [ key-value-list ]
        string? first = null;
        i = Skip(v, i, ref first);
        int s = i;
        while (i < v.Length && char.IsAsciiLetterOrDigit((char)v[i]))
        {
            i++;
        }

        r.ResultText = Encoding.ASCII.GetString(v, s, i - s).ToLowerInvariant();
        r.Result = r.ResultText switch
        {
            "none" => SpfResult.None,
            "neutral" => SpfResult.Neutral,
            "pass" => SpfResult.Pass,
            "fail" => SpfResult.Fail,
            "softfail" => SpfResult.SoftFail,
            "temperror" => SpfResult.TempError,
            "permerror" => SpfResult.PermError,
            _ => SpfResult.Unknown,
        };
        if (r.Result == SpfResult.Unknown)
        {
            D(s, i - s);
        }

        string? comment = null;
        i = Skip(v, i, ref comment);
        r.Comment = comment;

        while (true)
        {
            string? ignored = null;
            i = Skip(v, i, ref ignored);
            while (i < v.Length && v[i] == (byte)';')
            {
                i++;
                i = Skip(v, i, ref ignored);
            }

            if (i >= v.Length)
            {
                break;
            }

            int ks = i;
            while (i < v.Length && v[i] is not ((byte)'=' or (byte)';' or (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'('))
            {
                i++;
            }

            string key = Encoding.ASCII.GetString(v, ks, i - ks).ToLowerInvariant();
            i = Skip(v, i, ref ignored);
            if (key.Length == 0 || i >= v.Length || v[i] != (byte)'=')
            {
                D(ks, Math.Max(1, i - ks));
                if (key.Length == 0 && i < v.Length)
                {
                    // "=x": step over the equals sign and the value that goes with it, so it is one bad pair.
                    bool equals = v[i] == (byte)'=';
                    i++;
                    while (equals && i < v.Length && v[i] is not ((byte)';' or (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'('))
                    {
                        i++;
                    }
                }

                continue;
            }

            i++;
            i = Skip(v, i, ref ignored);
            string val;
            if (i < v.Length && v[i] == (byte)'"')
            {
                var bytes = new List<byte>();
                i++;
                while (i < v.Length && v[i] != (byte)'"')
                {
                    byte c = v[i++];
                    if (c == (byte)'\\' && i < v.Length)
                    {
                        c = v[i++];
                    }

                    bytes.Add(c);
                }

                if (i < v.Length)
                {
                    i++;
                }
                else
                {
                    D(i, 0);
                }

                val = ReportText.Decode([.. bytes]);
            }
            else
            {
                int vs = i;
                while (i < v.Length && v[i] is not ((byte)';' or (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'('))
                {
                    i++;
                }

                val = ReportText.Decode(v.AsSpan(vs, i - vs));
            }

            r._pairs.Add(new KeyValuePair<string, string>(key, val));
        }

        return r;
    }

    private static int Skip(byte[] v, int i, ref string? firstComment)
    {
        while (i < v.Length)
        {
            byte c = v[i];
            if (c is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                i++;
            }
            else if (c == (byte)'(')
            {
                var text = new List<byte>();
                int depth = 0;
                while (i < v.Length)
                {
                    byte x = v[i++];
                    if (x == (byte)'\\' && i < v.Length)
                    {
                        text.Add(v[i++]);
                    }
                    else if (x == (byte)'(')
                    {
                        if (depth++ > 0)
                        {
                            text.Add(x);
                        }
                    }
                    else if (x == (byte)')')
                    {
                        if (--depth == 0)
                        {
                            break;
                        }

                        text.Add(x);
                    }
                    else if (x is not ((byte)'\r' or (byte)'\n'))
                    {
                        text.Add(x);
                    }
                }

                firstComment ??= ReportText.Decode([.. text]).Trim();
            }
            else
            {
                break;
            }
        }

        return i;
    }
}
