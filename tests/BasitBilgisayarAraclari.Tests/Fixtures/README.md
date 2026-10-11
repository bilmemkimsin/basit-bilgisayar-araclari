# Test fixtures

`Rar.none.rar` is the 101,226-byte unencrypted RAR sample from SharpCompress 0.50.3's `tests/TestArchives/Archives/Rar.none.rar`. It is used only to verify this application's RAR preview and staging/apply path.

- Source: https://github.com/adamhathcock/sharpcompress/blob/0.50.3/tests/TestArchives/Archives/Rar.none.rar
- SHA-256: `C9F4233A877FBB2940FF981AE09FBA55D7854D5251D71E755268CE657C557CE2`
- License notice: `SharpCompress-TestArchives-LICENSE.txt`

`7z-symbolic-link.7z` is a 164-byte local fixture created with 7-Zip 26.04 using `-t7z -snl`. It contains one valid symbolic-link entry named `link.txt` whose relative target is `referent.txt` in the same directory. It is used only to verify that the application rejects 7z link entries before changing a target directory.

- Source: generated locally from a temporary same-directory symbolic link; no third-party archive content.
- SHA-256: `711B902C3653BC842B251C13D1D5FBF1D1FF9531203C8601713F0067DFF46BC6`
- License notice: not applicable; generated test fixture.
