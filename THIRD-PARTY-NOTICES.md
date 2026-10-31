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
- **NIST CFReDS Data Leakage Case** (fictional scenario; Windows 7 and Outlook 2013 disk image; https://cfreds-archive.nist.gov/data_leakage_case/data-leakage-case.html): NIST states that "information presented on NIST sites are considered public information and may be distributed or copied. Use of appropriate byline/photo/image credits is requested." Credit: NIST CFReDS. The Outlook offline store from the image is read by the PST tests and oracle comparisons; nothing from it is committed.
- **Apache Tika test documents** (`tika-parser-microsoft-module` and `tika-parser-mail-module`, `.msg`, `.pst` and `winmail.dat` data files, at a pinned commit; `tools/corpus/msg-corpus.tsv`): Apache License 2.0, (c) The Apache Software Foundation. Data files only; none is stored in this repository.
- **Apache POI test data** (`test-data/hsmf`, `.msg` files including minimised fuzzer cases, at a pinned commit): Apache License 2.0, (c) The Apache Software Foundation. Data files only; none is stored in this repository.
- **extract-msg 0.55.0** (Python package, GPL-3.0): run as an installed package by `tools/msg/compare_extract_msg.py` to compare results. It is not linked, copied or shipped with any OneMail package.
- **Apache POI test data** (`test-data/hmef`, six TNEF files including the minimised `oom.tnef`) and **Apache Tika** `testWINMAIL.dat`, at pinned commits (`tools/corpus/tnef-corpus.tsv`): Apache License 2.0, (c) The Apache Software Foundation. Data files only; none is stored in this repository.
- **tnefparse 1.4.0** (Python package, LGPL), **tnef 1.4.18** (GPL-2+) and **ytnef 2.1.2** (GPL-2+): run as installed programs by `tools/tnef/compare.py` to compare results. They are not linked, copied or shipped with any OneMail package.
