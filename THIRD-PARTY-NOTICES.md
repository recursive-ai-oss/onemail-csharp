# Third-party notices

OneMail's own code is licensed `MIT OR Apache-2.0`. Material from other projects that ships with a package, or is used by the tests, is listed here.

Nothing ships with a package yet.

## Test corpora (fetched, never committed)

`tools/corpus/fetch-mime-corpora.sh` downloads these for round-trip sweeps; no file from them is stored in this repository.

- **EDO Enron Email PST Dataset / CMU Enron Email Dataset** (EnronData.org; Carnegie Mellon University CALO project): the CMU May 2015 maildir is used. The EDO dataset is licensed under the Creative Commons Attribution 3.0 United States license; cite EnronData.org. The messages are real people's mail: results cite counts only, never content.
- **Dovecot imaptest mailbox** (`dovecot-crlf`, published by the Dovecot project as test input): used as an input file only.
- **Apache James Mime4j test messages** (`core/src/test/resources/testmsgs` and `mimetools-testmsgs`, message files only, at a pinned commit): Apache License 2.0, (c) The Apache Software Foundation.
