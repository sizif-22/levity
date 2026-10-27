# Vendored files

| File | Version | SHA-256 |
|---|---|---|
| mermaid.min.js | 11.10.1 (npm: `mermaid@11.10.1`, `dist/mermaid.min.js`, unmodified) | `0664267560d2f17d8e4dbaf010b58adfae8b57a7ac7325a11c77b45c24d4fc79` |

The hash is checked by `tests/repo/Validate-Repo.ps1`. To update Mermaid: change the pinned version in `web/package.json`, copy the new `dist/mermaid.min.js` here, and update this table, the notice in `THIRD-PARTY-NOTICES.md` and the diagram tests together. Test the result in a real browser, because Mermaid's layout and label handling change between versions.
