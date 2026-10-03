# XSR-777 Resource presentation language

Services retain both provider-original and Chinese enrichment metadata. The
Desktop presentation chooses those fields using the committed interface language,
independently of formatting culture, country/network policy and cached provider data.
English uses original title/description; Simplified/Traditional Chinese may use
Chinese enrichment. Missing enrichment falls back to the original content.

Apply the same selection to resource results, favorites, details and associated
installed mods/shaders/packs. Preserve local resource-pack formatted filenames and
metadata. Language changes update cached presentation without rerunning fingerprint
identification, resetting the search input or changing the download identity.

English does not request automatic Chinese descriptions. Retire translation work
on language change and reject late results; they cannot overwrite English content.
Resource-owned literal text is never passed through the interface string catalog.

Regressions cover language changes in lists and details, original metadata,
Chinese fallback, stale translation results, stable search focus and no extra
association queries. Validate Desktop, architecture and NativeAOT/trim CI.
