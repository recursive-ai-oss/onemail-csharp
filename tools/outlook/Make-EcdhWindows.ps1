# Makes Windows' own CMS encoder (CryptMsgOpenToEncode, the call Outlook's S/MIME goes through) encrypt to each ka-*.crt certificate in <Set> with ECDH key
# agreement (P-256 and P-384), in several scheme / key-wrap / identifier / UKM variants, and writes the EnvelopedData as <Set>\win\<cert>-<variant>.der.
# This shows what Windows writes and accepts, so the library's own ECDH messages and its reading can be compared with it. Test-side only; needs Windows
# PowerShell 5 (Add-Type with /unsafe), no Outlook and no private key.
#   Make-EcdhWindows.ps1 -Set <dir>
param([Parameter(Mandatory)][string]$Set)
$src = @'
using System; using System.Runtime.InteropServices; using System.Text;
public static unsafe class WinEcdh {
  [StructLayout(LayoutKind.Sequential)] public struct BLOB { public uint cb; public byte* pb; }
  [StructLayout(LayoutKind.Sequential)] public struct ALG { public byte* oid; public BLOB parms; }
  [StructLayout(LayoutKind.Sequential)] public struct BITBLOB { public uint cb; public byte* pb; public uint unused; }
  [StructLayout(LayoutKind.Sequential)] public struct CERTID { public uint choice; public BLOB a; public BLOB b; }
  [StructLayout(LayoutKind.Sequential)] public struct ENCKEY { public uint cbSize; public BITBLOB pub; public CERTID id; public long date; public void* other; }
  [StructLayout(LayoutKind.Sequential)] public struct KAGREE { public uint cbSize; public ALG kea; public void* kaux; public ALG wrap; public void* waux; public IntPtr prov; public uint keyspec; public uint keychoice; public ALG* eph; public BLOB ukm; public uint cRec; public ENCKEY** rec; }
  [StructLayout(LayoutKind.Sequential)] public struct RECIP { public uint choice; public KAGREE* ka; }
  [StructLayout(LayoutKind.Sequential)] public struct ENVELOPED { public uint cbSize; public IntPtr prov; public ALG content; public void* aux; public uint cRecipients; public void* rgpRecipients; public RECIP* rgCms; public uint cCert; public void* rgCert; public uint cCrl; public void* rgCrl; public uint cAttr; public void* rgAttr; public uint cUnprot; public void* rgUnprot; }
  [DllImport("crypt32.dll", SetLastError=true)] static extern IntPtr CryptMsgOpenToEncode(uint enc, uint flags, uint type, void* info, byte* inner, void* stream);
  [DllImport("crypt32.dll", SetLastError=true)] static extern bool CryptMsgUpdate(IntPtr h, byte* data, uint cb, bool fin);
  [DllImport("crypt32.dll", SetLastError=true)] static extern bool CryptMsgGetParam(IntPtr h, uint type, uint idx, byte* data, ref uint cb);
  [DllImport("crypt32.dll")] static extern bool CryptMsgClose(IntPtr h);
  static byte* Z(string s, ref IntPtr keep) { byte[] b = Encoding.ASCII.GetBytes(s + "\0"); IntPtr p = Marshal.AllocHGlobal(b.Length); Marshal.Copy(b, 0, p, b.Length); return (byte*)p; }
  static byte* Copy(byte[] b) { IntPtr p = Marshal.AllocHGlobal(Math.Max(1, b.Length)); Marshal.Copy(b, 0, p, b.Length); return (byte*)p; }
  public static byte[] Encrypt(string contentOid, string keaOid, string wrapOid, byte[] curveParams, byte[] pub, byte[] issuer, byte[] serialLe, byte[] ski, byte[] ukm, byte[] data, out string err) {
    err = null; IntPtr k = IntPtr.Zero;
    ALG eph = new ALG { oid = Z("1.2.840.10045.2.1", ref k), parms = new BLOB { cb = (uint)curveParams.Length, pb = Copy(curveParams) } };
    ENCKEY ek = new ENCKEY { cbSize = (uint)sizeof(ENCKEY), pub = new BITBLOB { cb = (uint)pub.Length, pb = Copy(pub), unused = 0 } };
    if (ski != null) { ek.id.choice = 2; ek.id.a = new BLOB { cb = (uint)ski.Length, pb = Copy(ski) }; }
    else { ek.id.choice = 1; ek.id.a = new BLOB { cb = (uint)issuer.Length, pb = Copy(issuer) }; ek.id.b = new BLOB { cb = (uint)serialLe.Length, pb = Copy(serialLe) }; }
    ENCKEY* pek = &ek;
    KAGREE ka = new KAGREE { cbSize = (uint)sizeof(KAGREE), keychoice = 1, cRec = 1, rec = &pek };
    ka.kea = new ALG { oid = Z(keaOid, ref k) }; ka.wrap = new ALG { oid = Z(wrapOid, ref k) };
    ka.eph = &eph;
    if (ukm != null) ka.ukm = new BLOB { cb = (uint)ukm.Length, pb = Copy(ukm) };
    RECIP r = new RECIP { choice = 2, ka = &ka };
    ENVELOPED env = new ENVELOPED { cbSize = (uint)sizeof(ENVELOPED), cRecipients = 1, rgCms = &r };
    env.content = new ALG { oid = Z(contentOid, ref k) };
    IntPtr h = CryptMsgOpenToEncode(0x10001, 0, 3, &env, null, null);
    if (h == IntPtr.Zero) { err = "open " + Marshal.GetLastWin32Error().ToString("x"); return null; }
    fixed (byte* d = data) { if (!CryptMsgUpdate(h, d, (uint)data.Length, true)) { err = "update " + Marshal.GetLastWin32Error().ToString("x"); return null; } }
    uint cb = 0; CryptMsgGetParam(h, 2, 0, null, ref cb);
    byte[] outb = new byte[cb];
    fixed (byte* o = outb) { if (!CryptMsgGetParam(h, 2, 0, o, ref cb)) { err = "getparam " + Marshal.GetLastWin32Error().ToString("x"); return null; } }
    CryptMsgClose(h);
    Array.Resize(ref outb, (int)cb);
    return outb;
  }
}
'@
$cp = New-Object System.CodeDom.Compiler.CompilerParameters; $cp.CompilerOptions = "/unsafe"; Add-Type -TypeDefinition $src -CompilerParameters $cp
function HexToBytes([string]$h) { $b = New-Object byte[] ($h.Length/2); for ($i=0;$i -lt $b.Length;$i++){ $b[$i]=[Convert]::ToByte($h.Substring(2*$i,2),16) }; ,$b }
$curve = @{ "ka-only" = [byte[]](0x06,0x08,0x2A,0x86,0x48,0xCE,0x3D,0x03,0x01,0x07); "ka-384" = [byte[]](0x06,0x05,0x2B,0x81,0x04,0x00,0x22) }
New-Item -ItemType Directory -Force "$Set\win" | Out-Null
$data = [Text.Encoding]::UTF8.GetBytes("Content-Type: text/plain; charset=utf-8`r`n`r`nwindows made body`r`n")
foreach ($f in Get-ChildItem $Set -Filter "ka-*.crt") {
  $n = $f.BaseName
  $c = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList $f.FullName
  $serial = HexToBytes $c.SerialNumber; [Array]::Reverse($serial)
  $ski = $null; foreach ($e in $c.Extensions) { if ($e.Oid.Value -eq "2.5.29.14") { $ski = $e.RawData[2..($e.RawData.Length-1)] } }
  foreach ($v in @(
    @("sha1-a128","1.3.133.16.840.63.0.2","2.16.840.1.101.3.4.1.5","2.16.840.1.101.3.4.1.2",$false,$false),
    @("sha1-a256","1.3.133.16.840.63.0.2","2.16.840.1.101.3.4.1.45","2.16.840.1.101.3.4.1.42",$false,$false),
    @("sha1-a256-ukm","1.3.133.16.840.63.0.2","2.16.840.1.101.3.4.1.45","2.16.840.1.101.3.4.1.42",$false,$true),
    @("sha1-a256-ski","1.3.133.16.840.63.0.2","2.16.840.1.101.3.4.1.45","2.16.840.1.101.3.4.1.42",$true,$false),
    @("sha256-a256","1.3.132.1.11.1","2.16.840.1.101.3.4.1.45","2.16.840.1.101.3.4.1.42",$false,$false),
    @("sha384-a256","1.3.132.1.11.2","2.16.840.1.101.3.4.1.45","2.16.840.1.101.3.4.1.42",$false,$false)
  )) {
    $err = $null
    $skiArg = if ($v[4]) { ,[byte[]]$ski } else { $null }
    $ukm = if ($v[5]) { [byte[]](1..32) } else { $null }
    $out = [WinEcdh]::Encrypt($v[3], $v[1], $v[2], $curve[$n], $c.GetPublicKey(), $c.IssuerName.RawData, $serial, $skiArg, $ukm, $data, [ref]$err)
    if ($out) { [IO.File]::WriteAllBytes("$Set\win\$n-$($v[0]).der", $out); "{0} {1}: {2} bytes" -f $n, $v[0], $out.Length } else { "{0} {1}: FAIL {2}" -f $n, $v[0], $err }
  }
}
