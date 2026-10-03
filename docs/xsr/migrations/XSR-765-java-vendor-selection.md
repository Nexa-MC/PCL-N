# XSR-765 — Preferred Java distribution

`java.vendor` becomes a validated choice of the existing Java brands; its empty default
means automatic. The value remains a string in the durable Settings contract, scoped
globally or by normalized instance directory. Unknown values are rejected rather than
implicitly interpreted as a Java brand.

The Java auto-selection preference carries an optional brand. Selection first filters
enabled, available, version-compatible candidates and preserves the existing minimum
major and JDK/JRE ranking. Vendor preference is a soft tie-break ahead of default brand
ranking. If that brand is absent or incompatible, normal compatible selection remains
available. Explicit executable selection never acquires or switches to another vendor.
Mojang runtime acquisition retains its authority and package contracts; this preference
does not claim to select a provider for runtime downloads.

Desktop renders the vendor choices in the same draggable segmented track as other
settings. A flexible right-aligned slot caps the track to the actual row width, while
retaining its intrinsic content width and horizontal scrolling. No dropdown, synchronous
probe or concrete Java service is introduced in presentation.

Regression covers preference versus compatibility/availability, fallback, explicit-path
precedence, persisted scoped selection, and narrow-window track bounds and scrolling.
