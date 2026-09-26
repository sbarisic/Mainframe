# Mainframe v1 wire protocol design

Status: approved v1 design with the Windows-local execution subset implemented.
The rest of v1 remains a target. See [plan.md](plan.md) for milestone boundaries
and `docs/protocol` for implemented schemas and golden wire fixtures.

## Implemented Windows-local subset

The host listens on IPv4 loopback with TLS 1.3. Operator connections use mTLS and
the `terminal` role. The `program` role uses server-authenticated TLS, followed by
restricted AUTH using a single-use inherited-pipe credential. Anonymous clients
cannot dispatch requests; enrollment and peers remain unavailable.

The connector requires `unary-rpc` and offers `streaming-v1`, `execution-v1`, and
`shell-v1`. WELCOME selects only offered features. Unary clients remain compatible.
The kernel implements the three kernel queries plus program registration/list/removal,
host-root administration, shell open/command, and process start/resize/interrupt.
See the [implemented contracts](docs/protocol/README.md) for argument/result types.

Connections multiplex up to 128 exchanges with increasing odd connector IDs;
acceptor-originated IDs use even numbers. No operation is replayed. Ordinary client
calls and process launches have a ten-second deadline; accepted programs continue
until exit, cancellation, lost authorization, or detected session loss. Network
loss can leave an uncertain outcome. Shutdown sends GOAWAY and drains for up to ten
seconds before connection cleanup and process termination.

DATA, WINDOW_UPDATE, END_STREAM, CANCEL, and COMPLETE are implemented. Receive
windows reserve 64 KiB each from a 6 MiB pool. Outgoing DATA reserves capacity from
a separate 6 MiB pool before copying; controls and pending request payloads each
have a 1 MiB bound. The remaining connection allowance covers current frame/JSON
processing. These are payload budgets; managed object overhead is separate.
When receive capacity is unavailable, the channel stays at zero credit. Outgoing
DATA waits for buffer capacity and is scheduled fairly; controls have priority.

A connection retains at most 4,096 active plus unretired exchange records.
With `exchange-retire-v1`, completed records are released through the retirement
handshake below; without it, this is a lifetime bound. Completed records retain
channel direction, remaining credit, and EOF state rather than response payloads.
Late authorized DATA is discarded without delivery; excess credit or illegal
transitions close the connection. Queued input is dropped after completion.
Exhausting server bookkeeping sends GOAWAY and closes the connection. IDs are never
reused. The host also bounds connections to 32 and concurrent TLS handshakes to 8.
Negotiated frame sizes range from 1 KiB to 1 MiB.

Local certificate maintenance requires the stopped-host state lock and uses a
recoverable journal. It renews only unexpired, unrevoked credentials in their final
24 hours. This is separate from future network enrollment and renewal.
Linux execution, remote storage, peer leases, and public endpoint acceptance remain
unimplemented and unverified.

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
| 0x0014 | RETIRE | Empty; negotiated exchange retirement |
| 0x0015 | RETIRE_ACK | Empty; negotiated retirement acknowledgement |
| 0x0020 | DATA | Raw bytes on a declared stream |
| 0x0021 | END_STREAM | Empty payload: sender will send no more data on the channel |
| 0x0022 | WINDOW_UPDATE | JSON: additional byte credit from the receiver |
| 0x0030 | PING | Eight-byte opaque token |
| 0x0031 | PONG | Echo of the PING token |
| 0x0032 | GOAWAY | JSON: shutdown reason and optional drain deadline |

HELLO, WELCOME, AUTH, AUTH_RESULT, PING, PONG, and GOAWAY use exchange/channel
zero. REQUEST, RESPONSE, COMPLETE, CANCEL, RETIRE, and RETIRE_ACK use a nonzero exchange and channel
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
  "clientName": "mframe",
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

Storage REQUEST for `fs.open`:

```json
{
  "method": "fs.open",
  "version": 1,
  "timeoutMs": 5000,
  "arguments": {
    "path": "/vol/documents/report.txt",
    "access": "read",
    "mode": "open-existing",
    "share": ["read"]
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
    "sessionId": "opaque-shell-id",
    "ioMode": "pipes"
  }
}
```

Arguments are an array. Optional `sessionId` selects a connection-owned shell working
directory; without it, the manifest supplies the host directory. `shell.command`
parses the deliberately small Mainframe shell language. No operation implicitly
evaluates an OS shell command string.

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
combined output channel, implemented locally with Windows ConPTY; Linux PTYs remain pending. Carry
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

## Local encrypted-storage protocol

Local Windows [encrypted volumes](plan.md#local-encrypted-volumes)
now implement these method-version-1 contracts. See `docs/protocol/storage-v1.schema.json` and the write transcript. Retain the 20-byte frame header, TLS, existing channel state machine,
limits, cancellation, and completed-exchange bookkeeping. Negotiate the explicit
`storage-v1` feature before dispatching storage calls. The current host advertises it alongside `streaming-v1`. A missing feature is an explicit
unsupported response, not fallback to host filesystem access.

| Methods | Contract outline |
| --- | --- |
| `volume.create` | Absolute local container path and sensitive password; returns volume UUID; leaves it unmounted |
| `volume.mount` | Existing local path, `/vol/<name>`, sensitive password; returns UUID, generation, and capabilities |
| `volume.list` | Bounded configured mount records, state, UUID, and provider capabilities; never key material |
| `volume.unmount` | Mount path; refuses busy volumes; flushes/checkpoints/closes before success |
| `fs.list` | Virtual path, requested batch size, opaque bounded continuation token; returns entries and next token |
| `fs.stat` | Exactly one virtual path or opaque handle; returns type, length, and timestamps |
| `fs.mkdir`, `fs.delete` | Virtual path; empty-only directory deletion, no recursive delete |
| `fs.rename` | Source, destination, explicit replace flag; atomic same-volume publication |
| `fs.open` | Virtual path, access, create mode, read/write/delete sharing flags; returns opaque handle and metadata |
| `fs.read`, `fs.write` | Handle, explicit offset, bounded length; one binary data channel |
| `fs.truncate` | Handle and new length |
| `fs.flush`, `fs.close` | Handle; distinct durability and lifetime operations |

Control messages use explicit JSON contracts with required fields, duplicate-name
rejection, and optional-field evolution. Encode all file lengths, offsets, and
actual byte counts as decimal strings in the nonnegative signed 64-bit range.
Batch sizes and ordinary bounded counts remain JSON numbers. Open modes are
`open-existing`, `create-new`, `open-or-create`, and `truncate-existing`; access is
`read`, `write`, or `read-write`. Validate incompatible mode/access combinations.
Mutation responses report committed status where applicable; storage COMPLETE
results have storage-specific contracts, not process exit codes.

Create/mount requests contain a sensitive `password` string with no password-specific
length or character restrictions. Empty strings are valid; the ordinary frame-size
limit still applies. They are operator-only over authenticated TLS. Disable payload logging and
redact before JSON/error diagnostics; never echo the field, include it in audit
records, or deliver it to a program session. Prompt input remains separate from
foreground program I/O. Password-bearing requests have no replay/idempotent retry.
Use dedicated bounded KDF admission (two concurrent per host), operator/global
unlock throttles, and return busy without retaining password-bearing queues.
A timed-out create/mount may already have committed; inspect state manually before
retrying. Generic unlock failures must not claim to distinguish wrong passwords
from damaged encrypted headers. Keys never travel with ordinary file operations.

Authorization is based on `volume.manage` for operator-only administration and
`volume:<uuid>:read`/`volume:<uuid>:write` for file operations, with the live lease
and frozen program permission intersection. A path or supplied identity is not an
authorization grant. Handles bind connection, caller, volume, and mount/host
generations. They are invalid after connection loss, execution end, lease expiry,
or restart. Mount state is local; peer leases and owner routing remain deferred.

Name resolution, local resource limits, mount lifecycle, journal handling, and
provider capabilities are specified in plan.md. Storage errors must distinguish
locked/unavailable/busy volumes, access/sharing denials, invalid/stale handles,
invalid names, missing/existing files, disk full, corruption, unsupported formats,
and I/O failures. The schema and explicit handlers define version-1 fields; errors include
`VOLUME_LOCKED`, `VOLUME_UNAVAILABLE`, `VOLUME_BUSY`, `ACCESS_DENIED`,
`SHARING_VIOLATION`, `INVALID_HANDLE`, `INVALID_PATH`, `NOT_FOUND`,
`ALREADY_EXISTS`, `DISK_FULL`, `UNLOCK_FAILED`, `CORRUPT_VOLUME`,
`UNSUPPORTED_FORMAT`, `NOT_SUPPORTED`, `CANCELLED`, and `IO_ERROR`. Retry advice never authorizes automatic mutation replay.

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
complete after the encrypted SQLite FULL commit for managed volumes. The later
host-directory provider will acknowledge OS acceptance. An explicit `fs.flush` requests durable flushing; close is not flush.
Provider capabilities declare these semantics. A partial
stream followed by connection loss must not be reported as a successful full read
or write.

For this milestone, both read and write requests are limited to 65,536 bytes.
The SDK splits larger operations. The read RESPONSE declares channel 1 with the
responder as sender; write declares channel 1 with the requester as sender. A
zero-length operation may complete as a unary response. For a nonempty write,
reserve bounded capacity, then grant credit. Require exactly the declared number
of bytes followed by END_STREAM before beginning the transaction. Short/overlong
input fails without a partial transaction. COMPLETE reports decimal-string `bytes` only
after commit. Reads report decimal-string `bytes` and `eof` after DATA and END_STREAM. EOF
means the requested range reached the committed file end, including zero-byte reads.
Flush and metadata operations are unary. A lost COMPLETE can leave a committed
write with an unknown outcome; cancellation cannot undo an acknowledged commit.

Each bounded managed write is atomic; a multi-request upload is not. Use a temporary
file and atomic same-volume rename/replace to publish a complete file. Cross-volume
rename returns an explicit unsupported error; version-2 handles support byte-range locks. Support owner-
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
  channels. Late CANCEL before retirement is idempotent; it cannot alter a completed result.
  After retirement, every frame for the exchange is a protocol error.
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


Local storage uses a 64-operation admission limit and serialized owner gate per
volume. Each connection reserves up to four 256 KiB storage scratch
budgets, including transfer buffers and provider chunk scratch, within the existing
16 MiB transport budget. Streaming transactions never wait for incoming DATA.
Storage RPC deadlines default to ten seconds and accept 1..30,000 ms. Timing out
or losing COMPLETE can leave a mutation outcome unknown; it is never replayed.
Metadata and positive authorization failures before mutation use `not_started`;
I/O/transport failures use `unknown` where commit cannot be excluded.


## Negotiated namespace, storage v2, and exchange retirement

Clients can offer `namespace-v1`, `storage-v2`, and `exchange-retire-v1` in HELLO.
The frame header and protocol major remain 1. Storage method version 2 requires
both namespace and storage-v2 negotiation; version-1 methods remain available.
The authenticated `filesystem` role uses operator mTLS and accepts only `fs.*` and
kernel discovery. It denies `volume.*`, execution, registration, and enrollment.
Every protected operation checks current resource grants; role or method access
alone is not permission to access all mounted volumes.

| Frame | Type | Exchange | Channel | Payload |
| --- | --- | --- | --- | --- |
| RETIRE | `0x0014` | Nonzero | 0 | Empty |
| RETIRE_ACK | `0x0015` | Nonzero | 0 | Empty |

After a terminal RESPONSE or COMPLETE, the requester stops producing exchange
traffic and waits for its already queued writes to finish before sending RETIRE.
The responder validates/drains previously authorized input until RETIRE, fences
its outbound exchange writes, and sends RETIRE_ACK. It then drops the completed
record. The requester drops its record when RETIRE_ACK arrives. Retired exchange
IDs are never reused; any further received frame for that ID is a protocol error.
Duplicate, wrong-direction, premature, or unnegotiated retirement is an error.

The writer's fence includes in-flight and queued controls and DATA. A racing local
producer cannot enqueue frames after the terminal boundary. Already-buffered
output remains readable after retirement and stays charged to the receive budget
until consumed or the connection closes. Retirement does not grant extra credit.
An uncooperative peer is bounded by the existing 128 active exchanges, 4,096
active/unretired records, and 16 MiB payload budget. Legacy peers keep the old
completed-record behavior. Exhaustion closes the connection; it never triggers
mutation replay.

See [storage-v2.schema.json](docs/protocol/storage-v2.schema.json) and the
[append/retirement transcript](docs/protocol/storage-write-v2-retire.hex). Detailed
method contracts and portable error mapping guidance are in
[the protocol reference](docs/protocol/README.md#writable-filesystem-method-version-2).
Version-2 file IDs are volume UUID plus stable entry ID; synthetic nodes use a
separate namespace identity. Handles, opaque continuation tokens, and locks remain
bound to connection and generation. Logical offsets, lengths, byte counts, and
space values are decimal strings. Attribute masks and batch limits are bounded
JSON numbers. Unsupported features are explicitly advertised by `fs.discover`.

Retirement acceptance covers 10,050 exchanges with bounded retained records,
cancellation racing completion, buffered output after retirement, rejection of
frames after retirement, and legacy bookkeeping exhaustion. A separate TLS storage
test reads a 257 MiB sparse file after 10,050 root queries on one connection. The
append/retirement golden transcript is checked against the actual frame encoder.
