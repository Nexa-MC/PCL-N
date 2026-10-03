# Settings migration: Java acquisition and memory

The dev checkout is inspected read-only. Legacy assembly shapes are not copied. This
slice enables existing layered contracts only after connecting their launch consumers:

- `java.auto-install`: disabled by default. When enabled, a missing compatible runtime
  proceeds through the existing acquisition planner and installer without an approval
  dialog. Explicit runtime errors and blocked acquisition ranges remain errors. An
  instance can override the global preference; cancellation still stops acquisition.
- `game.memory`: custom MiB values reach the launch request exactly, at global or instance
  scope. Instance Auto overrides global custom memory and uses the existing automatic
  loader-aware policy; it does not masquerade as zero or inherited memory. With no explicit
  layered preference, existing instance metadata and legacy settings retain their behavior.
- Auto is an explicit UI action beside the memory and Java editors. Empty memory text is
  invalid, while a blank Java path retains its existing Auto behavior. Instance reset only
  removes that instance's override. Values persist through SettingsPolicyService and the
  sealed settings routes; Desktop neither estimates memory nor selects Java.

The launch request consumes a single effective snapshot for memory. Auto does not claim
to be a learned estimate or a verified measurement; the estimator migration is separate.
Other reserved settings stay unavailable until their real consumers ship.

Regression gates cover restart persistence, independent instance overrides, Auto versus
custom, rejection without publication, actual prepared launch requests, Java approval
defaults and automatic acquisition, and Desktop command-to-value flow. Architecture,
format, NativeAOT services and Desktop AOT/trim CI remain required.
