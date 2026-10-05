# XSR-784: network preferences and transfer budgets

The Host captures committed global settings for each new HTTP request. Proxy modes are
`0` (direct), `1` (system), and `2` (custom). A custom proxy must be an absolute
HTTP, HTTPS or SOCKS5 origin URI with a host, optional port, and no userinfo, path,
query or fragment. Username/password are supplied separately, remain local-only,
and never appear in snapshot descriptions, ordinary UI value text or exports.
Pool generations belong to a Host session. Changing a preference selects a new
pool for later requests; active responses retain their original transport until
released. TLS verification, HTTP authority/redirect admission and artifact digests
remain the responsibility of the existing consumers and are never weakened.
FoundationHost owns the common pool and captures network preferences from one
committed effective snapshot. Composition-created clients preserve HTTP diagnostics.
The API Shield client receives a separate pool with the existing client certificate;
this pool follows the same network snapshot and preserves no-redirect TLS policy.
The Host binds the bandwidth limiter after SettingsPolicy construction before
publication of the Host; only the composition boundary may replace that binding.
Existing public constructor and Compose signatures remain available. Host-specific
HTTP clients are additive init-only transport overrides. `ComposeWithJavaRuntimeRoot`
adds an explicitly supplied managed-Java root while the original `Compose` signature
remains unchanged, including positional `null` observer calls.

`network.doh` defaults to true and resolves direct HTTP destinations with the
existing Cloudflare HTTPS JSON resolver, then the mainland-only doh.pub resolver;
failed or unusable responses fall back to system DNS. Bootstrap requests use
system DNS with certificate verification and no redirect authority expansion.
Their HTTPS transport follows the committed direct/system proxy mode: direct mode
disables proxies for both destination and DoH bootstrap requests.
Queries fetch A and AAAA, reject wrong DNS record types, bound response bytes,
and retain DNS TTLs for at most five minutes. Custom proxies own destination DNS;
system-proxy connections resolve the proxy itself through the system resolver.
Changing DoH cannot alter HTTPS SNI, Host headers, or metadata authority.

`network.ip-stack` is the global exportable enum `auto` / `ipv4` / `ipv6`, defaults
to `auto`, and applies to new requests. It orders TCP connection attempts without
disabling the other family; `auto` retains resolver order. Proxy TCP endpoints use
the same preference, while proxy destination DNS remains owned by the proxy.

`network.bandwidth-kib` is a global integer from 0 through 1048576 KiB/s, defaults
to 0 (unlimited), and is exportable with NextTask timing. Transfers capture their
budget generation at admission; all downloads using that generation share one
rate limit, including segmented connections. Accounting carries fractional
milliseconds on a monotonic clock, rounds positive waits upward to the supported
timer resolution, and retains at most one millisecond of idle credit. This bounds
short bursts without reducing every fast body read to an independent one-millisecond
wait. Cancellation releases the admission gate without discarding bytes already
charged to that budget generation. This is an application transfer-body budget; it does not
claim to constrain TLS/HTTP overhead, OS socket buffers, external tools or update
helper processes. The normal shared DownloadService consumes it for Minecraft,
loader/component, Authlib and online resource downloads. Independently streamed
Java files may opt into the same owned limiter at their install boundary.

Source preferences continue the existing regional contract: only CN may use
regional mirrors, authoritative metadata/checksums remain canonical, and artifacts
must retain their current digest/signature requirements. Existing game-file source
and retry preferences are captured for the additional loader/component/modpack
transfer paths where their source planners already provide safe candidates.
Modpack file workers also capture the existing bounded `network.file-concurrency`
and retry policies. Pinned pack URLs keep their order and allowed-origin checks;
parallel transfers synchronize expanded-byte accounting before publication.
Unimplemented content-provider enable/disable, background downloads and automatic
network diagnostics are not represented as completed merely by a stored value.
