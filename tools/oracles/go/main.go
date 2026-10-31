// godump is the Go oracle driver: it prints the normalised dump form (see tools/oracles/README.md) using only the
// standard library's net/mail, mime, mime/multipart, mime/quotedprintable and encoding/base64.
//
// Reads relative file names on standard input (one per line, under -root); writes one JSON line per input.
package main

import (
	"bufio"
	"bytes"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"mime"
	"mime/multipart"
	"mime/quotedprintable"
	"net/mail"
	"net/textproto"
	"os"
	"path/filepath"
	"runtime"
	"sort"
	"strings"
)

type Hdr struct {
	Name  string  `json:"name"`
	Raw   string  `json:"raw"`
	Value *string `json:"value"`
}

type Body struct {
	Len        int    `json:"len"`
	Sha256     string `json:"sha256"`
	EolSha256  string `json:"eolSha256"`
	TrimSha256 string `json:"trimSha256"`
}

type Entity struct {
	Headers     []Hdr             `json:"headers"`
	ContentType string            `json:"contentType"`
	Params      map[string]string `json:"params"`
	Disposition *string           `json:"disposition"`
	Filename    *string           `json:"filename"`
	CteApplied  string            `json:"cteApplied"`
	Preamble    *bool             `json:"preamble"`
	Epilogue    *bool             `json:"epilogue"`
	Body        *Body             `json:"body"`
	Children    []*Entity         `json:"children"`
	Message     *Entity           `json:"message"`
	Error       *string           `json:"error,omitempty"`
}

type Addr struct {
	Name string `json:"name"`
	Addr string `json:"addr"`
}

type Date struct {
	Utc           string `json:"utc"`
	OffsetMinutes int    `json:"offsetMinutes"`
}

type Oracle struct {
	Name           string `json:"name"`
	Version        string `json:"version"`
	Notes          string `json:"notes"`
	HeadersOrdered bool   `json:"headersOrdered"`
}

type Record struct {
	Schema    string            `json:"schema"`
	ID        string            `json:"id"`
	Oracle    Oracle            `json:"oracle"`
	Error     *string           `json:"error"`
	Defects   []string          `json:"defects"`
	Addresses map[string][]Addr `json:"addresses,omitempty"`
	Date      *Date             `json:"date"`
	Root      *Entity           `json:"root,omitempty"`
}

var unstructured = map[string]bool{"subject": true, "comments": true, "keywords": true, "content-description": true, "thread-topic": true}
var addressHeaders = []string{"From", "Sender", "Reply-To", "To", "Cc", "Bcc"}

// latin1 maps each octet to the code point of the same value, as every driver does for raw header text.
func latin1(s string) string {
	var b strings.Builder
	for i := 0; i < len(s); i++ {
		b.WriteRune(rune(s[i]))
	}
	return b.String()
}

func str(s string) *string { return &s }

func hashes(body []byte) *Body {
	h := func(b []byte) string { s := sha256.Sum256(b); return hex.EncodeToString(s[:]) }
	eol := bytes.ReplaceAll(body, []byte("\r\n"), []byte("\n"))
	eol = bytes.ReplaceAll(eol, []byte("\r"), []byte("\n"))
	return &Body{Len: len(body), Sha256: h(body), EolSha256: h(eol), TrimSha256: h(bytes.TrimRight(eol, "\n"))}
}

// headers lists the header fields. net/mail keeps a map, so the order across names is not available
// (oracle.headersOrdered is false); values within one name keep their order.
func headers(h textproto.MIMEHeader, decodeValues bool) []Hdr {
	keys := make([]string, 0, len(h))
	for k := range h {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	dec := new(mime.WordDecoder)
	out := []Hdr{}
	for _, k := range keys {
		for _, v := range h[k] {
			e := Hdr{Name: latin1(k), Raw: latin1(v)}
			if decodeValues && unstructured[strings.ToLower(k)] {
				// Without a charset reader the decoder knows UTF-8, ISO-8859-1 and US-ASCII; other charsets
				// are reported as "not provided" rather than as a difference.
				if d, err := dec.DecodeHeader(v); err == nil {
					e.Value = str(strings.ToValidUTF8(d, "\uFFFD"))
				}
			}
			out = append(out, e)
		}
	}
	return out
}

func entity(h textproto.MIMEHeader, body io.Reader, depth int) *Entity {
	e := &Entity{Headers: headers(h, true), Children: []*Entity{}}
	ctype, params, err := mime.ParseMediaType(h.Get("Content-Type"))
	switch {
	case h.Get("Content-Type") == "":
		ctype, params = "text/plain", map[string]string{}
	case err != nil && ctype == "":
		ctype, params = "text/plain", nil
	case err != nil:
		params = nil
	}
	e.ContentType, e.Params = ctype, params
	if d, dp, err := mime.ParseMediaType(h.Get("Content-Disposition")); err == nil {
		e.Disposition = str(d)
		if fn, ok := dp["filename"]; ok {
			e.Filename = str(strings.ToValidUTF8(fn, "\uFFFD"))
		}
	}
	if e.Filename == nil && params != nil {
		if n, ok := params["name"]; ok {
			e.Filename = str(strings.ToValidUTF8(n, "\uFFFD"))
		}
	}

	var reader io.Reader = body
	e.CteApplied = "none"
	switch strings.ToLower(strings.TrimSpace(h.Get("Content-Transfer-Encoding"))) {
	case "base64":
		e.CteApplied = "base64"
		reader = base64.NewDecoder(base64.StdEncoding, body)
	case "quoted-printable":
		e.CteApplied = "quoted-printable"
		reader = quotedprintable.NewReader(body)
	}

	boundary := ""
	if params != nil {
		boundary = params["boundary"]
	}
	if strings.HasPrefix(ctype, "multipart/") && boundary != "" && depth < 100 {
		// NextRawPart leaves Content-Transfer-Encoding alone; NextPart would decode quoted-printable itself and drop the header.
		mr := multipart.NewReader(reader, boundary)
		for {
			p, err := mr.NextRawPart()
			if err == io.EOF {
				break
			}
			if err != nil {
				e.Error = str(err.Error())
				break
			}
			e.Children = append(e.Children, entity(textproto.MIMEHeader(p.Header), p, depth+1))
		}
		return e
	}

	data, err := io.ReadAll(reader)
	if err != nil {
		e.Error = str(err.Error())
	}
	if ctype == "message/rfc822" && depth < 100 {
		m, merr := mail.ReadMessage(bytes.NewReader(data))
		if merr != nil {
			e.Error = str(merr.Error())
		} else {
			e.Message = entity(textproto.MIMEHeader(m.Header), m.Body, depth+1)
		}
		return e
	}
	e.Body = hashes(data)
	return e
}

func dump(id, path string) *Record {
	rec := &Record{Schema: "onemail-dump/1", ID: id, Oracle: Oracle{
		Name: "go", Version: runtime.Version(), HeadersOrdered: false,
		Notes: "net/mail.ReadMessage, mime.ParseMediaType, mime/multipart NextRawPart, base64.StdEncoding stream decoder (CR/LF skipped), quotedprintable.Reader",
	}}
	data, err := os.ReadFile(path)
	if err == nil {
		var m *mail.Message
		m, err = mail.ReadMessage(bytes.NewReader(data))
		if err == nil {
			rec.Addresses = map[string][]Addr{}
			for _, name := range addressHeaders {
				if m.Header.Get(name) == "" {
					continue
				}
				list, aerr := m.Header.AddressList(name)
				if aerr != nil {
					rec.Addresses[strings.ToLower(name)] = nil // null: the oracle could not parse the header
					continue
				}
				out := []Addr{}
				for _, a := range list {
					out = append(out, Addr{Name: strings.ToValidUTF8(a.Name, "\uFFFD"), Addr: strings.ToValidUTF8(a.Address, "\uFFFD")})
				}
				rec.Addresses[strings.ToLower(name)] = out
			}
			if m.Header.Get("Date") != "" {
				if t, derr := m.Header.Date(); derr == nil {
					_, off := t.Zone()
					rec.Date = &Date{Utc: t.UTC().Format("2006-01-02T15:04:05Z"), OffsetMinutes: off / 60}
				}
			}
			rec.Root = entity(textproto.MIMEHeader(m.Header), m.Body, 0)
		}
	}
	if err != nil {
		rec.Addresses, rec.Root, rec.Date = nil, nil, nil
		rec.Error = str(strings.ToValidUTF8(err.Error(), "\uFFFD"))
	}
	return rec
}

func main() {
	root := flag.String("root", ".", "directory the input names are relative to")
	version := flag.Bool("version", false, "print the Go version and exit")
	flag.Parse()
	if *version {
		fmt.Println("go " + runtime.Version())
		return
	}
	in := bufio.NewReaderSize(os.Stdin, 1<<16)
	out := bufio.NewWriterSize(os.Stdout, 1<<16)
	defer out.Flush()
	enc := json.NewEncoder(out)
	enc.SetEscapeHTML(false)
	for {
		line, err := in.ReadString('\n')
		line = strings.TrimRight(line, "\r\n")
		if line != "" {
			rec := dump(line, filepath.Join(*root, line))
			if e := enc.Encode(rec); e != nil {
				fmt.Fprintln(os.Stderr, "encode:", e)
			}
		}
		if err != nil {
			break
		}
	}
}
