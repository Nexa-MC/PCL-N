# Interface localization

Desktop owns embedded presentation catalogs for Simplified Chinese (`zh-Hans`),
Traditional Chinese (`zh-Hant`) and English (`en`). The existing `general.language`
global setting stores `auto|zh-Hans|zh-Hant|en`; writes reuse sealed settings routes,
are durable before publication, and apply immediately. `auto` follows the host's
UI culture; unsupported system languages use English. Legacy locale spellings are
normalized on read. Language choice never changes protocol IDs, persisted enum
values, logs, version identities or paths.

UI.Next accepts an optional presentation-only text resolver. It resolves captions,
accessible names and input placeholders before measuring and scene publication.
Input drafts, rich content and explicitly literal text remain untouched. Desktop
marks filenames, profile names, installed identifiers and provider content literal.
Changing the language invalidates text layout once, without recreating the tree,
discarding drafts, changing focus or issuing network queries.

Catalogs use source captions and numbered templates as stable presentation keys.
Exact matches precede bounded template matching; missing translations retain the
source text. Captured arguments are never translated. Dictionaries and a bounded
per-language result cache keep render-time work local. Catalog loading uses explicit
JSON parsing, with no reflection or dynamic assembly loading, for NativeAOT.

Regression coverage must include all three languages, system fallback, missing keys,
template argument preservation, language persistence/validation, live remeasurement,
accessible labels, placeholders, and unchanged user text/focus/drafts. Run architecture
gates and NativeAOT/trim shell validation for the final change.

Validated on 2026-10-01: 424 Services tests, 100 Desktop composition tests,
88 UI.Next tests, 8 Avalonia backend scenarios and architecture gates for 32
projects. Windows NativeAOT and link-trimmed Desktop shells ran with each of
`zh-Hans`, `zh-Hant` and `en` persisted in isolated settings stores; both first-run
shell checks also completed. Embedded PXML captions and every catalog entry are
checked for translation coverage, argument preservation and encoding integrity.
