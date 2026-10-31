// Runs the corpus through Go's mime package and prints one JSON line per case (see py_oracle.py for the format).
//
//	ew:    mime.WordDecoder.DecodeHeader, with golang.org/x/text supplying the charsets the standard library lacks
//	param: mime.ParseMediaType on the Content-Disposition value
package main

import (
	"bufio"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"mime"
	"os"
	"strings"

	"golang.org/x/text/encoding/htmlindex"
	"golang.org/x/text/encoding/ianaindex"
	"golang.org/x/text/transform"
)

type testCase struct {
	ID     int    `json:"id"`
	Kind   string `json:"kind"`
	Hex    string `json:"hex"`
	Hdr    string `json:"hdr"`
	Want   string `json:"want"`
}

func charsetReader(label string, input io.Reader) (io.Reader, error) {
	enc, err := ianaindex.MIME.Encoding(label)
	if err != nil || enc == nil {
		enc, err = htmlindex.Get(label)
		if err != nil {
			return nil, err
		}
	}
	return transform.NewReader(input, enc.NewDecoder()), nil
}

func main() {
	f, err := os.Open(os.Args[1])
	if err != nil {
		panic(err)
	}
	defer f.Close()
	out := bufio.NewWriter(os.Stdout)
	defer out.Flush()
	sc := bufio.NewScanner(f)
	sc.Buffer(make([]byte, 1<<20), 1<<24)
	dec := &mime.WordDecoder{CharsetReader: charsetReader}
	for sc.Scan() {
		var c testCase
		if err := json.Unmarshal(sc.Bytes(), &c); err != nil {
			panic(err)
		}
		res := map[string]any{"id": c.ID}
		if c.Kind == "cs" {
			res["s"] = 1
		} else if c.Kind == "ew" {
			raw, _ := hex.DecodeString(c.Hex)
			if !isASCII(raw) {
				res["s"] = 1
			} else if s, err := dec.DecodeHeader(string(raw)); err != nil {
				res["e"] = errName(err)
				res["r"] = hex.EncodeToString([]byte(s))
			} else {
				res["r"] = hex.EncodeToString([]byte(s))
			}
		} else {
			_, params, err := mime.ParseMediaType(c.Hdr)
			if v, ok := params[c.Want]; ok {
				res["r"] = hex.EncodeToString([]byte(v))
			} else if err != nil {
				res["e"] = errName(err)
			} else {
				res["n"] = 1
			}
		}
		b, _ := json.Marshal(res)
		fmt.Fprintln(out, string(b))
	}
}

func isASCII(b []byte) bool {
	for _, c := range b {
		if c >= 0x80 {
			return false
		}
	}
	return true
}

func errName(err error) string {
	s := err.Error()
	if i := strings.Index(s, ":"); i > 0 {
		s = s[:i]
	}
	return s
}
