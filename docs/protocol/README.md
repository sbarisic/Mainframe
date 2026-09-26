# Implemented local protocol

The Windows host implements protocol major 1 over TLS 1.3. Operator devices use
client certificates and the `terminal` role. Supervised programs use the `program`
role, validate the inherited CA, and redeem a 30-second single-use credential with
AUTH. AUTH_RESULT is `{"ok":true}` on success; failed authentication closes the
connection. No unauthenticated operational requests are dispatched.

HELLO requires `unary-rpc`; current clients also offer `streaming-v1`, `execution-v1`,
and `shell-v1`. WELCOME selects offered features only. Existing unary clients still
work. Peers, filesystem clients, and enrollment are not implemented.

## Contracts

[`unary-v1.schema.json`](unary-v1.schema.json) defines common envelopes.
[`execution-v1.schema.json`](execution-v1.schema.json) defines execution payloads.
Select definitions using the frame type and RPC method below. Unknown JSON fields
are ignored; duplicate properties, missing required properties, and nesting beyond
32 levels are rejected. .NET string bounds use UTF-16 code units.

| Method, version 1 | Arguments | Successful result |
| --- | --- | --- |
| `kernel.describe`, `kernel.health`, `kernel.capabilities` | Empty object | Query object |
| `program.register` | `register` | Registration with qualifiedName, revision, manifest, incompatibility |
| `program.list` | Empty object | Registration array |
| `program.remove` | `selector` with a qualified name | Empty object |
| `host-root.add` | `root` | Root object |
| `host-root.list` | Empty object | Root array |
| `host-root.remove` | `selector` | Empty object |
| `shell.open` | Empty object | `shellState` |
| `shell.command` | `shellCommand` | `shellState` for built-ins, streamed `started` for programs |
| `process.start` | `start` | Streamed `started` |
| `process.resize`, `process.interrupt` | `control` | Empty object |

Every shell and execution-control ID is scoped to its owning connection. Short
program selectors must be unambiguous. `sessionId` on process.start selects a
shell's current directory; otherwise the manifest supplies the directory. Resize
and interrupt require terminal mode. Pipe-mode cancellation uses CANCEL instead.

Pipe mode declares channel 1 (requester stdin), 2 (responder stdout), and 3
(responder stderr). Terminal mode declares 1 (stdin) and 2 (combined terminal
output). Raw DATA preserves bytes. Receivers issue WINDOW_UPDATE `{"bytes":N}`
only after reserving capacity. Initial channels have zero credit; the implementation
uses 64 KiB windows and accepts at most 256 KiB outstanding credit on a send channel.

END_STREAM closes a unidirectional channel. COMPLETE uses the response envelope
with `streaming:false` and an `exited` result, after every responder output EOF.
Exit codes are signed 32-bit Windows process exit-code representations. A nonzero
exit code is a completed execution, not an RPC failure. COMPLETE can end remaining
input; callers must concurrently consume both output streams when using pipe mode.

Launch deadlines apply before acceptance; they do not limit runtime. Cancellation
requests termination, not rollback. Failures after launch can have outcome `unknown`.
The client never replays requests. Stream cancellation keeps other exchanges usable.

## Bounds and lifecycle

Frames negotiate 1 KiB..1 MiB, with DATA bounded to 64 KiB and available credit.
There are at most 128 active exchanges and eight channels each. Reserved input
windows and queued output DATA each have a 6 MiB budget; controls and pending
request payloads each have a 1 MiB bound. Current-frame processing uses the remaining
allowance within the 16 MiB payload budget. Object metadata is separately bounded
by the exchange/channel limits.

Completed records keep channel credit/direction/EOF state, without retaining the
RPC response. Only previously authorized late DATA can be discarded. The 4,096
lifetime exchange-record bound closes the connection instead of forgetting IDs.
Late CANCEL is harmless; over-credit DATA, repeated EOF, wrong-direction traffic,
or premature COMPLETE is a protocol error. Data is scheduled fairly across
channels and control frames have priority.

PING/PONG operates independently of blocked data streams. After ten idle seconds
the connection sends a ping; after thirty seconds without valid traffic it closes.
GOAWAY rejects new requests and allows up to ten seconds to drain. Foreground
cleanup starts on detected loss, with at most two seconds before force termination.

The bootstrap pipe contains a `bootstrap` object, bounded to 16 KiB, then EOF.
The token is never sent in argv, environment variables, or terminal output. Program
RPC authority expires with its execution or authorization lease. Leases last 60
seconds and renew only while the execution, operator, and frozen permissions remain
authorized; expired leases cannot be revived.

## Wire fixtures

Each `.hex` file contains one full application frame before TLS encryption:

- `hello-v1.hex`: original unary HELLO, preserving compatibility.
- `process-start-v1.hex`: connector exchange 17 starts `hello Alice` in pipe mode.
- `window-update-v1.hex`: exchange 17, channel 1 grants 65,536 bytes.
- `complete-v1.hex`: exchange 17 completes with exit code 7.

Golden tests compare source-generated contracts against these bytes. They are
individual frames, not a complete bidirectional transcript. See
[packets.md](../../packets.md) for the broader v1 design and deferred peer/storage
and enrollment contracts.

## Planned encrypted-storage contracts

Storage is the next local Windows milestone, not part of the current schemas or
wire fixtures. See [plan.md](../../plan.md#next-milestone-local-encrypted-volumes)
for implementation and acceptance, and
[packets.md](../../packets.md#local-encrypted-storage-protocol-planned) for contracts.

Add `storage-v1` feature negotiation and explicit method-version-1 contracts for
volume administration, metadata, handles, and binary reads/writes. Keep the existing
frame format. Storage lengths/offsets/byte counts are decimal strings; one read or
write request transfers at most 64 KiB. END_STREAM is not a commit acknowledgement;
write COMPLETE follows the transaction commit. Storage completion results are not
`ProcessExited`. Existing process fixtures, including exit code 7, remain unchanged.

The implementation must add `storage-v1.schema.json` and golden fixtures/transcripts
for open/read/write/flush/close, mount states, bounded enumeration, and failures.
Include ordered RESPONSE/DATA/END_STREAM/COMPLETE exchanges, zero-length operations,
wrong direction, declared-length mismatch, cancellation before/after commit,
late authorized DATA, failed authentication, and stale handles. Only synthetic
passwords may appear in fixtures, clearly labeled as test data. Runtime traces and
errors must redact create/mount passwords before serialization or logging.

Schemas and wire fixtures are intentionally not fabricated in this documentation
change. Add them with source-generated contracts and executable protocol tests.
Native encryption, WAL recovery, disk-full, and crash-injection tests also remain
required; a valid wire transcript does not demonstrate storage durability.
