# Mainframe v1 wire protocol design

Status: approved v1 design target with an initial unary subset implemented. This
document expands the resolved decisions in [plan.md](plan.md). The frame format
and message assignments below are selected for v1. JSON examples illustrate the
contracts; the implemented subset's schema and wire fixture are in `docs/protocol`.

## Implemented Windows-local subset

The first milestone implements the frame codec, strict JSON controls, TLS 1.3 mTLS,
HELLO/WELCOME, unary REQUEST/RESPONSE, PING/PONG, and GOAWAY. It negotiates only the
`unary-rpc` feature and `terminal` role; WELCOME reports `authenticated`. The server
listens on IPv4 loopback only. Local initialization provisions the initial operator;
network AUTH enrollment and program bootstrap are not implemented yet.

Implemented syscalls are `kernel.describe`, `kernel.health`, and
`kernel.capabilities`, all version 1 with an empty arguments object. The client
serializes calls with 10-second deadlines and never retries. Server unary handlers
run serially, accept optional request timeouts of 1..30,000 ms, and emit only final
responses. The server rechecks operator authorization on every frame and idle tick.

The host limits connections to 32, TLS handshakes to 8, and unary exchanges to
4,096 per connection. Request IDs must be increasing odd integers; a high-water
mark rejects reuse. Late CANCEL for a completed unary exchange is harmless. Stream
frames are rejected; stream windows and terminal-exchange stream records below are
future work, not claims of implemented behavior. Negotiated frames must be between
1 KiB and 1 MiB. No process, storage, peer delegation, or public network acceptance
is advertised. Shutdown of this subset closes connections; a full streaming drain
will accompany streaming support. Linux execution remains unverified.

## Transport and encryption

Use TCP protected by TLS 1.3 for every connection, including loopback, on
Windows and Linux. In .NET, use `SslStream` over the TCP stream. Establish TLS
before sending Mainframe frames. Validate server identity; use mutual TLS for
kernel peers and enrolled operator devices, with scoped bootstrap authentication
for program sessions. Do not silently
fall back to plaintext or accept arbitrary certificates.

TLS encrypts and authenticates the application headers and payloads. Do not add
custom per-packet cryptography. Network addresses and traffic timing remain
observable. Encryption is per connection: a trusted intermediary kernel can read
and forward the traffic it relays. This is not end-to-end encryption hiding data
from those kernels.

Raw UDP is not the initial transport: it would require reliability, ordering,
duplicate handling, and congestion control. QUIC is a possible later transport
with reliable multiplexed streams and integrated TLS 1.3. Its native streams would
replace parts of the framing/multiplexing design below; do not assume this TCP
mapping should be copied unchanged. Keep application contracts transport-neutral.

Implement custom RPC framing with `System.Text.Json` controls and binary DATA.
No gRPC or other RPC framework is selected. Modules register explicit typed method
contracts and handlers; do not use reflection-based object serialization or
arbitrary type activation.

## Frame layout

TCP is a byte stream, so one socket read may contain part of a frame or several
frames. Read the fixed header, validate it, then read exactly the declared payload.

All integer fields are unsigned and big-endian. There is no padding.

| Offset | Field | Bytes | Meaning |
| --- | --- | --- | --- |
| 0 | Payload length | 4 | Payload bytes, excluding the header |
| 4 | Message type | 2 | Message discriminator |
| 6 | Flags | 2 | Must be zero in version 1 |
| 8 | Exchange ID | 8 | Operation correlation ID |
| 16 | Channel ID | 4 | Stream within the exchange, or zero for control |
| 20 | Payload | Variable | UTF-8 JSON or raw bytes, depending on type |

The header is 20 bytes. Maximum payload is 1 MiB (1,048,576 bytes),
including during the handshake. Peers may negotiate a smaller maximum, never a
larger one. Use data chunks of at most 64 KiB by default, capped by the negotiated
limit and available stream credit. No compression is proposed for version 1.

Reject excessive lengths before allocating payload buffers. Apply the limits
below to JSON nesting, exchanges, channels, and aggregate buffering. Unknown
message types, nonzero reserved flags, invalid IDs, and illegal state transitions
are protocol errors in version 1. An incomplete frame at EOF is a truncated
connection, not a completed operation.

### Initial limits

| Setting | Default |
| --- | ---: |
| Maximum frame payload | 1 MiB |
| DATA chunk | 64 KiB |
| JSON nesting | 32 levels |
| Concurrent exchanges per connection | 128 |
| Channels per exchange | 8 |
| Outstanding credit per channel | 256 KiB maximum |
| Aggregate inbound/outbound buffering per connection | 16 MiB |
| TLS handshake deadline | 10 seconds |
| Application handshake deadline | 10 seconds |
| Idle heartbeat interval | 10 seconds |
| Unresponsive connection timeout | 30 seconds |
| Graceful shutdown drain | 10 seconds |

The aggregate budget covers incoming/outgoing queued payloads and reserved receive
capacity. Do not grant credit without capacity to honor it. Enforce limits before
allocation/acceptance rather than merely monitoring memory afterward. Public
endpoints also require configurable global/per-address connection and handshake
limits, authentication throttling, and bounded enrollment requests.

### Version evolution

HELLO advertises supported protocol majors; WELCOME selects the highest mutual
major. Reject connections without a common major. Negotiate optional features
explicitly and reject unsupported required features. Unknown JSON fields may be
ignored within a major version; duplicate properties and missing required fields
are errors. Reject unknown frame types and reserved flags. Breaking method changes
require a new method version. Schemas and wire fixtures must cover these rules.

Exchange ID zero is reserved for connection messages. The connecting endpoint
allocates odd IDs, and the accepting endpoint allocates even IDs. IDs are unique
for the connection lifetime and never reused. Replies retain the original ID.
Channel ID zero is reserved for exchange/connection control; stream IDs are
nonzero and scoped to their exchange. No total order is promised across channels.

## Message types

| Value | Message | Payload and purpose |
| --- | --- | --- |
| 0x0001 | HELLO | JSON: supported protocol versions, role, frame limit |
| 0x0002 | WELCOME | JSON: selected version, kernel identity, authentication state |
| 0x0003 | AUTH | JSON: negotiated application authentication mechanism and proof |
| 0x0004 | AUTH_RESULT | JSON: authentication success or failure |
| 0x0010 | REQUEST | JSON: method, contract version, arguments, optional timeout |
| 0x0011 | RESPONSE | JSON: unary result, stream acceptance, or rejection |
| 0x0012 | COMPLETE | JSON: final streaming operation result or error |
| 0x0013 | CANCEL | JSON: best-effort cancellation reason |
| 0x0020 | DATA | Raw bytes on a declared stream |
| 0x0021 | END_STREAM | Empty payload: sender will send no more data on the channel |
| 0x0022 | WINDOW_UPDATE | JSON: additional byte credit from the receiver |
| 0x0030 | PING | Eight-byte opaque token |
| 0x0031 | PONG | Echo of the PING token |
| 0x0032 | GOAWAY | JSON: shutdown reason and optional drain deadline |

HELLO, WELCOME, AUTH, AUTH_RESULT, PING, PONG, and GOAWAY use exchange/channel
zero. REQUEST, RESPONSE, COMPLETE, and CANCEL use a nonzero exchange and channel
zero. DATA, END_STREAM, and WINDOW_UPDATE use both a nonzero exchange and channel.

JSON examples below show payloads only. Exchange and channel IDs are in the
header. Contracts use portable types; represent potentially large 64-bit file
offsets and lengths as decimal strings to avoid JavaScript integer precision loss.
Frame IDs remain binary integers and require an appropriate integer type in SDKs.

## Connection lifecycle

1. Establish TLS and validate identities.
2. Connector sends HELLO; acceptor selects a mutually supported version in WELCOME.
3. Resolve certificate identity and current authorization. Without a client
   certificate, accept only enrollment or explicit process-bootstrap authentication,
   never ordinary operational requests before successful authentication.
4. Exchange requests and stream data within negotiated limits.
5. GOAWAY stops new requests and allows outstanding operations to drain until its
   10-second deadline, then closes the connection. Exchanges without terminal responses have
   failed or uncertain outcomes; socket closure is not success.

HELLO example:

```json
{
  "versions": [1],
  "optionalFeatures": [],
  "requiredFeatures": [],
  "role": "terminal",
  "clientName": "mf",
  "maxFrameBytes": 1048576
}
```

WELCOME example:

```json
{
  "version": 1,
  "features": [],
  "kernelId": "node-a",
  "mainframeId": "atlas",
  "maxFrameBytes": 1048576,
  "authentication": "required"
}
```

Roles are terminal, program, peer, or filesystem. A requested role is not a grant
of authority. Authentication, delegated scopes, and per-operation authorization
determine access. Enrollment is a restricted authentication flow, not a privileged
role claim. Never log authentication proofs or credentials.
Reject unsupported versions explicitly rather than attempting to reinterpret them.

### Enrollment and authentication lifecycle

`cluster init` establishes the cluster CA and coordinator identity. Nodes and
operator devices generate private keys locally and submit certificate requests.
Administrators issue random, single-use, 15-minute invitations specifying identity,
role, coordinator address, and CA fingerprint. Verify the fingerprint before sending
the invitation over TLS. Invitation possession is sufficient approval.

Use AUTH/AUTH_RESULT for restricted enrollment or program-bootstrap authentication;
these paths do not permit normal REQUEST dispatch before authentication. Enrollment
consumption is transactional in the coordinator database. A retry with the same
invitation and public key retrieves its original enrollment result, not a second
identity. The invitation cannot enroll another key. Expired or revoked identities
must reenroll using a new invitation.

Certificates last seven days and renew in the final 24 hours while authorized.
Enrolled kernels/operators use mutual TLS. Certificate validity alone does not
establish current permissions: revocation prevents lease renewal and requests
termination of connected sessions. Store keys under restrictive service-account
permissions; never include secrets in diagnostic or public backup output.

A supervised program receives a single-use 30-second bootstrap credential through
an explicitly inherited anonymous pipe/file descriptor separate from stdin.
Environment variables contain endpoint and handle identifier only. AUTH redeems
that credential for a process-scoped RPC session over server-authenticated TLS;
renewal requires a live authorized execution. This is the sole operational-program
exception to the client-certificate requirement, not anonymous operational access.

Grant users resource permissions and intersect them with manifest permissions for
programs. Peer calls carry coordinator-signed, audience-bound authorization leases
lasting 60 seconds. Validate signature, recipient, execution identity, scope, and
expiry, allowing at most five seconds of clock skew. A forwarding peer may narrow
but never expand authority. Unsigned caller identity fields are never trusted.

When the coordinator is unavailable, reject new sessions, starts, opens, membership
changes, and grant changes. Existing work may continue only until its current lease
expires; then reject protected operations and initiate process cleanup.

### Heartbeats

After 10 seconds of application-level inactivity, send PING with an opaque token.
Match PONG to that token. Treat a connection with no valid application traffic for
30 seconds as unresponsive and terminate it. Bound heartbeat/control traffic too.
Foreground cleanup starts at detected loss; silent loss is not detected instantly.

## Requests and results

Example REQUEST for `fs.open`:

```json
{
  "method": "fs.open",
  "version": 1,
  "timeoutMs": 5000,
  "arguments": {
    "path": "/vol/documents/report.txt",
    "access": "read"
  }
}
```

Example unary RESPONSE (final; no COMPLETE follows):

```json
{
  "ok": true,
  "streaming": false,
  "result": {
    "handle": "opaque-server-issued-handle",
    "length": "4821"
  }
}
```

Example rejected RESPONSE:

```json
{
  "ok": false,
  "error": {
    "code": "RESOURCE_UNAVAILABLE",
    "message": "The volume owner is offline.",
    "outcome": "not_started"
  }
}
```

Use stable codes such as UNSUPPORTED_METHOD, ACCESS_DENIED, NOT_FOUND,
RESOURCE_UNAVAILABLE, DEADLINE_EXCEEDED, CANCELLED, and INTERNAL_ERROR. Human
messages are diagnostic, not control signals. Errors do not expose stack traces
or secrets. Outcomes may be not_started, completed, or unknown; completed does
not imply success. A lost response to a mutation generally leaves its outcome
unknown. Request IDs alone do not provide exactly-once execution or cross-connection
deduplication. Do not automatically replay mutations after reconnection.

Timeouts are durations starting at receipt; a forwarding kernel subtracts elapsed
time and never resets the remaining budget. Timeout or CANCEL does not roll back
already committed effects. Calls requiring deduplication will need an explicit,
persistent idempotency contract separate from exchange IDs.

Handles are opaque, bound to authorized callers and an owning session/generation.
Reads, seeks, and close calls route to the owning kernel. A restart or reconnection
must not silently reinterpret a stale handle as a new resource.

## Process execution and terminal sessions

REQUEST:

```json
{
  "method": "process.start",
  "version": 1,
  "arguments": {
    "program": "cat",
    "argv": ["/vol/documents/report.txt"],
    "workingDirectory": "/",
    "ioMode": "pipes"
  }
}
```

Arguments are an array. Execution of a shell command string is a separate explicit
operation, not implicit quoting or concatenation by `process.start`.

Streaming RESPONSE:

```json
{
  "ok": true,
  "streaming": true,
  "result": {
    "processId": "opaque-process-id",
    "channels": [
      { "id": 1, "name": "stdin", "sender": "requester" },
      { "id": 2, "name": "stdout", "sender": "responder" },
      { "id": 3, "name": "stderr", "sender": "responder" }
    ]
  }
}
```

Channels are unidirectional. Declare them before sending DATA. After the receiver
grants credit for each channel, an exchange can proceed as follows:

```text
Client -> Server  DATA         exchange=17 channel=1  [stdin bytes]
Client -> Server  END_STREAM   exchange=17 channel=1
Server -> Client  DATA         exchange=17 channel=2  [stdout bytes]
Server -> Client  DATA         exchange=17 channel=3  [stderr bytes]
Server -> Client  END_STREAM   exchange=17 channel=2
Server -> Client  END_STREAM   exchange=17 channel=3
Server -> Client  COMPLETE     exchange=17 channel=0
```

COMPLETE payload:

```json
{
  "ok": true,
  "result": { "exitCode": 0 }
}
```

Streaming RESPONSE means execution was accepted/started; COMPLETE carries the
final result. A nonzero program exit code is an execution result, not necessarily
a transport failure. COMPLETE follows all responder output and END_STREAM frames.
It also terminates remaining input: a process may exit before stdin reaches EOF.
Specify bounded handling of already-in-flight input during completion/cancellation
using the terminal-exchange rules below. Rejected requests have no streams or COMPLETE.

Pipe mode preserves exact bytes and separate stdout/stderr. Terminal mode uses a
combined output channel, implemented with Windows ConPTY and Linux PTYs. Carry
initial dimensions and forward resize, EOF, and interrupts. Restore client terminal
settings on every exit. Resize and process signals are explicit operations; CANCEL
requests operation cancellation and is not a universal substitute for every signal.
On detected session loss, immediately initiate foreground process cleanup, allow
at most two seconds for graceful termination, then force termination and revoke
execution credentials. Use Windows Job Objects/Linux process groups for cleanup.
Explicit background jobs survive terminal disconnection. No interactive-session
reattachment or reconnect grace period is included in v1.

Process lookup accepts a short name only for one eligible registration. Ambiguous
names fail with candidates; use `namespace/name@version#node` to qualify. Freeze
registration revisions per execution. Manifests list compatible platforms,
interpreters, I/O modes, and requested permissions. Never turn argv into an implicit
host-shell command. Shell v1 supports quoted arguments and working-directory changes,
not pipelines, redirection, expansion, or implicit host-shell evaluation.

## File streaming

Use raw DATA rather than JSON/base64 for file bytes. Example request:

```json
{
  "method": "fs.read",
  "version": 1,
  "arguments": {
    "handle": "opaque-server-issued-handle",
    "offset": "0",
    "length": "65536"
  }
}
```

The RESPONSE declares a responder-to-requester data channel. After receiving
credit, the server sends DATA, END_STREAM, then COMPLETE with actual byte count
and end-of-file status. Write requests use a requester-to-responder channel and
complete after SQLite commit for managed volumes or OS acceptance for host-directory
volumes. An explicit `fs.flush` requests durable flushing; close is not flush.
Provider capabilities declare these semantics. A partial
stream followed by connection loss must not be reported as a successful full read
or write.

Each bounded managed write is atomic; a multi-request upload is not. Use a temporary
file and atomic same-volume rename/replace to publish a complete file. Cross-volume
rename and byte-range locks return explicit unsupported errors. Support owner-
enforced read/write/delete sharing flags. Do not cache content/metadata across
RPC calls or automatically replay mutations. Directory listings use bounded batches
and can reflect concurrent changes between batches. Full naming/provider rules are
in [plan.md](plan.md#namespace-and-storage).

## Flow control and cancellation

Each declared channel starts with zero byte credit. Its receiver grants credit
with WINDOW_UPDATE, for example `{"bytes":65536}`. DATA consumes payload-byte
credit; additional grants are additive, positive, bounded to 256 KiB outstanding
per channel, and checked for overflow.
END_STREAM is not gated by byte credit. Receivers replenish credit as data is
consumed, not merely copied into another unbounded buffer.

Use the initial limits above for queues, aggregate buffering, exchanges, and channels.
Schedule DATA fairly between channels and prioritize control frames. TCP still imposes
connection-wide ordering on the wire; control prioritization cannot bypass bytes
already sent or loss recovery. Rate-limit control messages as well.

CANCEL is best effort. The original exchange still needs a terminal RESPONSE or
COMPLETE indicating its actual outcome when possible. Cancellation may race with
normal completion. Peer disconnection never proves that a remote process stopped.
Keep a bounded terminal-exchange record so normal cancellation races are not
confused with protocol violations:

- Never reuse IDs or reopen completed exchanges. Stop producing new DATA when a
  terminal result is received; queued unsent input is discarded.
- Record remaining receive credit, channel directions, and closure state at
  completion. Discard late DATA only against previously granted credit; decrement
  that credit and never grant more. A credit overrun is a protocol error.
- Validate and drain already-in-flight stream control frames without reviving
  channels. Late CANCEL is idempotent; it cannot alter a completed result.
- Bound terminal-exchange bookkeeping. If preserving records would exceed the
  configured bound, send GOAWAY and close rather than forget state and risk treating
  late frames as new work. The implementation must publish and test this bound.
- Reconnection is a new session and does not replay mutations or restore handles.

## Kernel forwarding

Kernel A resolves a target and creates its own exchange with kernel B. It maps
exchange/channel IDs, preserves operation semantics, propagates remaining
deadlines and cancellation, and relays output with bounded buffering. IDs are
connection-local, not cluster-global process or resource identities.

Only authenticated peers may provide validated delegated caller context. An
untrusted client cannot impersonate another user by including identity fields.
Peer request metadata can include a trace ID and a decreasing hop limit:

```json
{
  "route": {
    "remainingHops": 8,
    "traceId": "opaque-trace-id"
  }
}
```

The kernel resource directory determines routing; node addresses do not belong
in every frame header. Exhausted hop limits fail explicitly. A generic capability
advertisement is not authority to serve an arbitrary file or device.

## Implementation and validation

Implement in stages: framing and TLS; handshake/authentication; unary kernel calls;
streaming process execution; bounded flow control and cancellation; peer relay.
Do not call the protocol ready until its state machine and resource limits are
specified and tested.

Required scenarios include fragmented headers/payloads, multiple frames per read,
oversized lengths, malformed JSON, invalid sequencing, incompatible versions,
unauthorized peers, wrong certificates, slow readers, channel interleaving, EOF,
process exit before input EOF, cancellation races, and disconnection during writes.

The implementation must supply JSON schemas, wire fixtures, and platform adapters
that enforce this selected design. Verify invitation/certificate expiry and replay,
revocation, public throttling, authorization lease expiry, qualified program lookup,
storage durability, and terminal restoration as well as framing behavior. Test on
real Windows and Linux before claiming cross-platform support. Runtime acceptance
is not established by this document revision. No alternative RPC framework, automatic
mutation retry, QUIC mapping, or reconnectable session is part of v1.

## References

- [Mainframe architecture and milestones](plan.md)
- [.NET SslStream](https://learn.microsoft.com/en-us/dotnet/api/system.net.security.sslstream?view=net-10.0)
- [QUIC transport specification](https://www.rfc-editor.org/rfc/rfc9000.html)
- [QUIC security specification](https://www.rfc-editor.org/info/rfc9001/)
- [.NET QUIC support](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/quic/quic-overview)
