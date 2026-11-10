# Owned PDF test fixtures

Generated for this repository using ReportLab, Pillow and pypdf; they contain only test text and a generated image, with no third-party document content.

- `copy-protected.pdf`: readable page text; copying disallowed by PDF permissions.
- `scan-copy-protected.pdf`: generated raster page; copying disallowed.
- `opening-protected.pdf`: encrypted with the test opening password `test-open-password`.

The owner password is `test-owner-password`. These are public test values. Tests exercise actual text extraction, page rendering and opening-password errors; no external PDF application is required.
