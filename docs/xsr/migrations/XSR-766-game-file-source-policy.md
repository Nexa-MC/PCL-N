# XSR-766 — Game file source selection

Global `network.game-source` offers official first, mirrors first, or official only.
The install and launch-completion batch captures this preference alongside concurrency
and retry. Source policy reorders or narrows the already authorized planner output; it
never invents a mirror for a file without a digest. All downloads retain mandatory
byte/length/hash verification and commit-last publication.

Metadata remains on authoritative routes regardless of this preference. Outside mainland
China the existing regional policy selects original sources only. Legacy source values
0/1/2 map to mirrors first/official first/official only; “MirrorOnly” in the legacy enum
does not remove its historic original-source fallback. Unsupported artifact mirror
replacements retain the original source.

The setting explicitly covers Minecraft game files and libraries, including launch
asset-index repair. Resource providers, Java acquisition and executable loader installers
retain their own trust and source contracts. UI enables the existing source-policy catalog
position and uses the right-aligned draggable selector.

Regression checks hashed/unhashed authority, all source orders, foreign regional behavior,
legacy persistence, and actual install/launch request destinations without external HTTP.
