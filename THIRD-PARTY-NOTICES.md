# Third-party notices

OneMail's own code is licensed `MIT OR Apache-2.0`. Material from other projects that ships with a package, or is used by the tests, is listed here.

One table ships in `RecursiveAI.OneMail.Calendar`: the Windows time zone names (next section). Nothing else ships with a package yet.

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


## RTF test data and oracles

- `testdata/rtf` holds the worked RTF example of MS-OXRTFEX section 3.1 (the source HTML and the encapsulated RTF, with the page-wrap damage of the published text repaired), and `tests/OneMail.Tests/Rtf` holds the two compressed-RTF examples of MS-OXRTFCP section 3.1 as hex. Both documents are Microsoft Open Specifications, covered by the Microsoft Open Specification Promise; the examples are quoted to test conformance.
- The RTF, HTML and text bodies that `tools/rtf` extracts from the Apache Tika and Apache POI test `.msg` files (Apache License 2.0, (c) The Apache Software Foundation; data files only, fetched by `tools/corpus/fetch-msg.sh`) are used for comparison runs and are never committed.
- `tools/rtf` runs two released Python packages as black-box programs, never linked, copied or shipped: `compressed-rtf` 1.0.7 (MIT) and `RTFDE` 0.1.2.2 (LGPL-3.0-or-later).

- **Apache POI test data** (`test-data/hmef`, six TNEF files including the minimised `oom.tnef`) and **Apache Tika** `testWINMAIL.dat`, at pinned commits (`tools/corpus/tnef-corpus.tsv`): Apache License 2.0, (c) The Apache Software Foundation. Data files only; none is stored in this repository.
- **tnefparse 1.4.0** (Python package, LGPL), **tnef 1.4.18** (GPL-2+) and **ytnef 2.1.2** (GPL-2+): run as installed programs by `tools/tnef/compare.py` to compare results. They are not linked, copied or shipped with any OneMail package.

- **Apache Tika test documents** (`testPST.pst` and `testPST_variousBodyTypes.pst` from the Microsoft parser module's `test-documents`, at a pinned commit; data files only): Apache License 2.0, (c) The Apache Software Foundation.

## Windows time zone names (ships in `RecursiveAI.OneMail.Calendar`)

`src/OneMail.Calendar/TimeZones/WindowsZones.g.cs` is generated by `tools/calendar/make_windows_zones.py` from the Unicode CLDR `windowsZones.xml` (release 47, sha256 `39714d7a5a276508aeca2289a490dddb8424dea0063f3f5fbc8bd8520dcbfcba`): it maps Windows zone names to the IANA zone of the default territory. Unicode License V3 (https://www.unicode.org/license.txt) requires this notice to travel with the data:

> UNICODE LICENSE V3
>
> COPYRIGHT AND PERMISSION NOTICE
>
> Copyright (c) 1991-2025 Unicode, Inc.
>
> NOTICE TO USER: Carefully read the following legal agreement. BY DOWNLOADING, INSTALLING, COPYING OR OTHERWISE USING DATA FILES, AND/OR SOFTWARE, YOU UNEQUIVOCALLY ACCEPT, AND AGREE TO BE BOUND BY, ALL OF THE TERMS AND CONDITIONS OF THIS AGREEMENT. IF YOU DO NOT AGREE, DO NOT DOWNLOAD, INSTALL, COPY, DISTRIBUTE OR USE THE DATA FILES OR SOFTWARE.
>
> Permission is hereby granted, free of charge, to any person obtaining a copy of data files and any associated documentation (the "Data Files") or software and any associated documentation (the "Software") to deal in the Data Files or Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, and/or sell copies of the Data Files or Software, and to permit persons to whom the Data Files or Software are furnished to do so, provided that either (a) this copyright and permission notice appear with all copies of the Data Files or Software, or (b) this copyright and permission notice appear in associated Documentation.
>
> THE DATA FILES AND SOFTWARE ARE PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT OF THIRD PARTY RIGHTS.
>
> IN NO EVENT SHALL THE COPYRIGHT HOLDER OR HOLDERS INCLUDED IN THIS NOTICE BE LIABLE FOR ANY CLAIM, OR ANY SPECIAL INDIRECT OR CONSEQUENTIAL DAMAGES, OR ANY DAMAGES WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THE DATA FILES OR SOFTWARE.
>
> Except as contained in this notice, the name of a copyright holder shall not be used in advertising or otherwise to promote the sale, use or other dealings in these Data Files or Software without prior written authorization of the copyright holder.

## Calendar comparison programs (run, never shipped)

The recurrence and calendar checks in `tools/calendar` run these released packages as separate processes and compare their output with ours. None is linked into, referenced by or shipped with a package, and no source of any of them was read.

| Program | Version | Licence |
|---|---|---|
| icalendar (PyPI) | 7.3.0 | BSD-2-Clause |
| python-dateutil (PyPI) | 2.9.0.post0 | Apache-2.0 or BSD-3-Clause |
| recurring-ical-events (PyPI) | 3.8.2 | LGPL-3.0-or-later (run as a separate process only) |
| x-wr-timezone (PyPI) | 2.0.1 | LGPL-3.0-or-later (run as a separate process only) |
| tzdata (PyPI) | 2026.6 | Apache-2.0 |
| vobject (PyPI) | 0.9.9 | Apache-2.0 |
| Ical.Net (NuGet) | 5.2.3 | MIT |
| FolkerKinzel.VCards (NuGet) | 8.2.0 | MIT |

## Outlook for Mac archive test data and oracles (fetched, never committed)

- **OLMReader sample archives** (`src/test/resources/Outlook16/outlook_example.olm` and `bill_rapp.olm`, at a pinned commit; `tools/corpus/olm-corpus.tsv`) and the element schemas the reader's layout follows (`src/main/resources/schema/*.xsd`, read as schema documents only): BSD 2-Clause License, Copyright (c) 2016, Tom Everett. All rights reserved. Redistribution and use in source and binary forms, with or without modification, are permitted provided that the copyright notice, the list of conditions and the disclaimer of that licence are retained. Data files only; none is stored in this repository. The bill_rapp archive holds messages of the public Enron e-mail data set (Creative Commons Attribution 3.0 United States; cite ZL Technologies, Inc.), and nothing from it is quoted.
- **OLM comparison programs** (run as separate processes, never linked or shipped): the olmreader 1.9.0 jar (Maven Central, BSD 2-Clause, Copyright (c) 2016 Tom Everett) and the olm-reader 1.0.0 npm package (Apache-2.0). No source of either was read.
