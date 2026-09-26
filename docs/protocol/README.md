# Implemented local protocol

The Windows host implements protocol major 1 over TLS 1.3. Operator devices use
client certificates and the `terminal` role. Supervised programs use the `program`
role, validate the inherited CA, and redeem a 30-second single-use credential with
AUTH. AUTH_RESULT is `{"ok":true}` on success; failed authentication closes the
connection. No unauthenticated operational requests are dispatched.

HELLO requires `unary-rpc`; current clients also offer `streaming-v1`, `execution-v1`,
`shell-v1`, `storage-v1`, `namespace-v1`, `storage-v2`, and `exchange-retire-v1`. WELCOME selects offered features only. Existing unary clients still
work. Peers and network enrollment are not implemented. Local SDK file access and
the separate Windows WinFsp adapter use the same filesystem contracts. The adapter
authenticates with the operator certificate in the restricted `filesystem` role
and requires `namespace-v1`, `storage-v2`, and `exchange-retire-v1`.

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
request payloads each have a 1 MiB bound. Current-frame processing and storage
transfer reservations each use another 1 MiB within the 16 MiB payload budget.
Object metadata is separately bounded
by the exchange/channel limits.

Completed records keep channel credit/direction/EOF state, without retaining the
RPC response. Only previously authorized late DATA can be discarded. The 4,096
active/unretired record bound closes the connection instead of forgetting live state.
Peers without `exchange-retire-v1` retain the lifetime bound.
Before retirement, late CANCEL is harmless; over-credit DATA, repeated EOF, wrong-direction traffic,
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
[packets.md](../../packets.md) for the broader v1 design and deferred peer/remote-storage
and enrollment contracts.

## Encrypted-storage contracts

Local Windows storage negotiates `storage-v1` plus `streaming-v1`. See
[storage-v1.schema.json](storage-v1.schema.json),
[plan.md](../../plan.md#local-encrypted-volumes), and
[packets.md](../../packets.md#local-encrypted-storage-protocol).
The JSON schema contains method-specific request definitions and typed results;
unknown optional properties remain allowed. The parser rejects duplicate properties.
Numeric bounds that JSON string schemas cannot express are enforced by handlers.

`storage-write-v1.hex` contains a complete interleaved application transcript:
requester REQUEST, responder streaming RESPONSE, responder WINDOW_UPDATE,
requester DATA (`abc`), requester END_STREAM, and responder COMPLETE after commit.
The fixture is compared byte-for-byte with explicit source-generated contracts.
It contains no credentials. TLS records and handshake frames are not included.
Storage completion uses `bytes` and `eof`, distinct from `ProcessExited`.
The existing process fixtures, including exit code 7, remain unchanged.

Integration tests exercise zero-length files, bounded transfers, truncated/overlong
writes, cancellation, independent connections, sharing errors, locked mounts,
wrong passwords, and actual program SDK access. Provider tests and child-host crash
injection cover encryption and transaction durability separately from framing.
Current Windows storage has no remote ownership or WinFsp protocol extension.

Password-bearing create/mount requests are operator-only. Typed request `ToString`
does not expose secrets. Passwords may be empty or contain any Unicode characters;
there is no password-specific length limit beyond the common RPC frame bound.
Runtime logs contain generic failure or volume identity events,
not payloads. Golden fixtures must never contain real passwords or credentials.


## Writable filesystem method version 2

All methods below require negotiated `namespace-v1`, `storage-v2`, and the existing
streaming feature. [storage-v2.schema.json](storage-v2.schema.json) defines payloads.
Use explicit `version: 2` in REQUEST. Version-1 handles use version-1 methods;
version-2 handles use version-2 methods. Both enforce the same sharing and locks.

| Method | Arguments definition | Result |
| --- | --- | --- |
| `fs.discover` | `path` | `discovery`: metadata, capabilities, deduplicated backing capacity |
| `fs.open` | `open` | `opened`: handle, metadata, created/opened/replaced action |
| `fs.stat` | `stat` | `entry`; exactly one path or handle |
| `fs.enumerate` | `enumerate` | `listing`; sorted batch and optional opaque continuation |
| `fs.read` | v1 `range` | Existing DATA/END_STREAM/COMPLETE byte-count contract |
| `fs.write` | `write` | Same transfer contract, with append/constrained options |
| `fs.rename` | `rename` | `committed` |
| `fs.metadata` | `metadata` | `committed` |
| `fs.size` | `size` | `committed`; allocation mode shrinks EOF when necessary |
| `fs.disposition` | `disposition` | `committed`; durable set/clear deletion intent |
| `fs.cleanup`, `fs.close`, `fs.flush` | `handle` | `committed` |
| `fs.lock`, `fs.unlock` | `lock` | `committed`; unlock matches offset, length and mode |
| `fs.flush-volume` | `path` | `committed`; selects one unlocked volume |

Create, open, open-or-create, overwrite, overwrite-or-create, and supersede are
owner-serialized transactional dispositions. Overwrite truncates the existing
object; supersede creates a new identity while version-2 handles retain the old
object. Directory overwrite/supersede is rejected. Kind is file, directory, or
either. Default rights are read-data/read-metadata; share flags default to none.
Data/list/metadata-read rights require volume read. Data-write/append/metadata-write/
delete rights require volume write. Metadata-only handles do not reserve data
sharing access. Read/write/delete sharing is enforced at the kernel across clients.

Enumeration accepts limit 1..256, optional restart or initial marker, and a
continuation bound to handle/generation. Restart and continuation are mutually
exclusive; marker and continuation are mutually exclusive. Each request executes a
bounded ordered query, so concurrent changes may affect later batches. Root `/`
contains only `vol`; `/vol` filters configured volumes by current read grants and
includes `state: locked`. Locked contents return VOLUME_LOCKED after authorization.

Attributes use the portable Windows-compatible values: read-only 1, hidden 2,
system 4, archive 32, temporary 256. Other bits are rejected. Timestamp setters use
ISO 8601 round-trip format. Reads do not update access time. Allocation is logical
and thin; storedBytes reports surviving chunk payload bytes, not encrypted database
size. BackingStorage reports shared host capacity, not per-volume quotas.

Delete intent requires delete rights, compatible sharing, a writable entry, and
an empty directory. It rejects new opens and conflicting namespace mutations.
Cleanup releases sharing/locks and detaches the name when no uncleaned handles
remain. I/O through retained version-2 handles may continue until final close.
Restart/disconnect finalizes accepted deletion intent; this is recovery, not RPC
replay. Version-1 handles are invalidated when their name/object is removed or
replaced. Atomic replacement preserves source identity and retains old destination
objects until their final handles close.

Locks are fail-fast, shared/exclusive, handle-owned, limited to 256 per handle and
4,096 per host. Reads conflict with another handle's exclusive lock; writes and
size changes conflict with shared locks (including their own) and another handle's exclusive lock.
This follows [Windows byte-range access rules](https://learn.microsoft.com/en-us/windows/win32/fileio/locking-and-unlocking-byte-ranges-in-files). Cleanup,
disconnect, and authorization loss release locks. Append chooses EOF inside the
transaction; constrained writes cannot extend EOF and report actual committed bytes.
Every write remains at most 64 KiB and receives exactly its declared DATA plus EOF
before committing. Completion loss may leave an unknown committed outcome.

The `filesystem` role requires the existing operator certificate and allows only
filesystem RPCs and kernel queries. It cannot administer volumes, start processes,
register programs, or enroll identities. Native Windows security descriptors and
NTSTATUS mapping are implemented in `Mainframe.WinFsp`, outside the wire contracts.

| Portable error | Current adapter Windows mapping |
| --- | --- |
| `NOT_FOUND` | STATUS_OBJECT_NAME_NOT_FOUND |
| `PATH_NOT_FOUND` | STATUS_OBJECT_PATH_NOT_FOUND |
| `ALREADY_EXISTS` | STATUS_OBJECT_NAME_COLLISION |
| `ACCESS_DENIED` | STATUS_ACCESS_DENIED |
| `SHARING_VIOLATION` | STATUS_SHARING_VIOLATION |
| `LOCK_CONFLICT` | STATUS_FILE_LOCK_CONFLICT |
| `LOCK_NOT_HELD` | STATUS_RANGE_NOT_LOCKED |
| `DELETE_PENDING` | STATUS_DELETE_PENDING |
| `DIRECTORY_NOT_EMPTY` | STATUS_DIRECTORY_NOT_EMPTY |
| `NOT_DIRECTORY`, `IS_DIRECTORY` | STATUS_NOT_A_DIRECTORY / STATUS_FILE_IS_A_DIRECTORY |
| `INVALID_HANDLE` | STATUS_INVALID_HANDLE |
| `VOLUME_LOCKED` | STATUS_DEVICE_NOT_READY |
| `NOT_SUPPORTED` | STATUS_NOT_SUPPORTED |

These mappings cover backend RPC errors. WinFsp enforces Windows sharing and
range locks within one mount; the adapter opens backend handles with all sharing
flags and does not forward Windows lock requests. SDK locks still apply to I/O
that reaches the kernel, but SDK clients and separate mounts do not share Windows
lock or cache coherence guarantees. See the
[adapter acceptance record](../../plan.md#winfsp-adapter-september-26-2026) for the
tested scope and pending manual GUI checks.

See [packets.md](../../packets.md#negotiated-namespace-storage-v2-and-exchange-retirement)
for retirement boundaries and [storage-write-v2-retire.hex](storage-write-v2-retire.hex)
for a golden append transfer followed by RETIRE/RETIRE_ACK. Both retirement frames
have empty payload, nonzero exchange ID, and channel zero.

Validation includes schema-1 migration rollback, all open dispositions, retained
objects, locked namespaces and grant filtering, handle-bound enumeration across
concurrent changes, per-handle/host lock quotas, filesystem-role denial, permanent
handle invalidation after observed authorization loss, and real host termination
around accepted deletion and replacement. The golden write/retirement transcript
is generated independently and compared byte-for-byte by the protocol tests.

The backend milestone passed **186 Windows Release tests** on 2026-09-26; the
subsequent adapter acceptance record reports **220 tests** for the full solution
in both Debug and Release. No new frames or method versions were added for WinFsp.
Unknown optional JSON fields are ignored during both decoding and volume-queue selection;
only the selected method's declared path or handle controls routing.
