# Third-party notices

OneMail's own code is licensed `MIT OR Apache-2.0`. Material from other projects that ships with a package, or is used by the tests, is listed here.

Nothing ships with a package yet.

## RFC example blocks

`testdata/calendar/rfc` holds the `BEGIN:VCALENDAR` and `BEGIN:VCARD` example blocks cut from RFC 2426, 2445, 2446, 5545, 5546, 6047 and 6350 (the IETF documents are © IETF Trust and the persons identified as the document authors). They are used under the IETF Trust Legal Provisions, which license code components extracted from RFCs under the Revised BSD License.

Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following conditions are met: redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer; redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided with the distribution; neither the name of the IETF Trust nor the names of specific contributors may be used to endorse or promote products derived from this software without specific prior written permission. This software is provided by the copyright holders and contributors "as is" and any express or implied warranties are disclaimed.

`testdata/reports` holds messages we generated that quote header and field examples from RFC 2557, 3464, 7208 and 8601 (same IETF Trust Legal Provisions and Revised BSD terms as above, notice repeated there).

## Test corpora (fetched, never committed)

`tools/corpus/fetch.sh` downloads these for round-trip and differential sweeps (pinned URL and SHA-256 in `tools/corpus/corpora.tsv`); no file from them is stored in this repository.

- **CMU Enron Email Dataset** (May 7 2015 version, maildir; Carnegie Mellon University CALO project, https://www.cs.cmu.edu/~enron/): no licence is stated; it is distributed as a research resource and asks users to be sensitive to the privacy of the people involved. The messages are real people's mail: results cite counts only, never content, and nothing from it is committed, quoted or uploaded.
- **Dovecot imaptest mailbox** (`dovecot-crlf`, published by the Dovecot project as test input): used as an input file only.
- **Apache James Mime4j test messages** (`core/src/test/resources/testmsgs` and `mimetools-testmsgs`, message files only, at a pinned commit): Apache License 2.0, (c) The Apache Software Foundation.

## RTF test data and oracles

- `testdata/rtf` holds the worked RTF example of MS-OXRTFEX section 3.1 (the source HTML and the encapsulated RTF, with the page-wrap damage of the published text repaired), and `tests/OneMail.Tests/Rtf` holds the two compressed-RTF examples of MS-OXRTFCP section 3.1 as hex. Both documents are Microsoft Open Specifications, covered by the Microsoft Open Specification Promise; the examples are quoted to test conformance.
- The RTF, HTML and text bodies that `tools/rtf` extracts from the Apache Tika and Apache POI test `.msg` files (Apache License 2.0, (c) The Apache Software Foundation; data files only, fetched by `tools/corpus/fetch-msg.sh`) are used for comparison runs and are never committed.
- `tools/rtf` runs two released Python packages as black-box programs, never linked, copied or shipped: `compressed-rtf` 1.0.7 (MIT) and `RTFDE` 0.1.2.2 (LGPL-3.0-or-later).
