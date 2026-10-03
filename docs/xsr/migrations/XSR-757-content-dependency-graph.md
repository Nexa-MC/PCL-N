# XSR-757 content dependency canvas

Desktop projects local declarations onto a generic, sealed UI.Next graph (keys, labels,
status and directed edges). Services retain parsing and relationship semantics; neither
the renderer nor its Avalonia backend references Minecraft contracts. One immutable scene
snapshot and one canvas render all points, arrows and selected labels, without a control
per mod. Node radius grows with the number of incoming edges. Deterministic bounded layout
is computed only when graph data changes, with no continuous force simulation.

Canvas supports node selection, drag pan, zoom/reset controls and search highlighting.
Selection details retain missing/disabled/ambiguous/cycle/unknown evidence; a declaration
graph is not proof of runtime loading or verified version compatibility. Limits are 10,000
nodes and 50,000 edges; over-limit input is rejected rather than silently losing evidence.
Tests cover directed edges, degree sizing, immutable projection and hit testing after pan
and zoom. Existing list pagination is replaced, not kept as a second graph navigation model.
