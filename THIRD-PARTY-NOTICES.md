# Third-party notices

OneMail's own code is licensed `MIT OR Apache-2.0`. Material from other projects that ships with a package, or is used by the tests, is listed here.

Nothing ships with a package yet.

## Test corpora (fetched, never committed)

`tools/corpus/fetch.sh` downloads these for round-trip and differential sweeps (pinned URL and SHA-256 in `tools/corpus/corpora.tsv`); no file from them is stored in this repository.

- **CMU Enron Email Dataset** (May 7 2015 version, maildir; Carnegie Mellon University CALO project, https://www.cs.cmu.edu/~enron/): no licence is stated; it is distributed as a research resource and asks users to be sensitive to the privacy of the people involved. The messages are real people's mail: results cite counts only, never content, and nothing from it is committed, quoted or uploaded.
- **Dovecot imaptest mailbox** (`dovecot-crlf`, published by the Dovecot project as test input): used as an input file only.
- **Apache James Mime4j test messages** (`core/src/test/resources/testmsgs` and `mimetools-testmsgs`, message files only, at a pinned commit): Apache License 2.0, (c) The Apache Software Foundation.
