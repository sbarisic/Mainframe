# Mainframe plan

Status: approved v1 design, with the Windows-local kernel, program execution, and
interactive shell milestones implemented. The complete distributed v1 runtime
remains a target; see the verified scope below. Windows-local password-unlocked encrypted volumes and SDK file access are implemented;
see storage validation and limitations below. The virtual root and writable Windows
VFS backend are implemented; the actual Windows adapter remains a separate milestone.

## Vision

Build a modular, mainframe-style computer environment in .NET 10 under the MIT
license. Multiple physical machines should present one logical mainframe:
one identity, namespace, permissions model, job system, and service inventory.
The sci-fi character should come from a coherent operator interface and real
subsystem behavior: boot profiles, health states, event journals, and recovery
tools.

The central design is a network of cooperating kernels. Each running kernel
provides the subset of syscalls supported by its modules and resources. Kernels
discover these capabilities and invoke one another over authenticated network
connections. A single-node installation uses the same contracts and routing model.

## Current implementation

Verified on this Windows machine on 2026-09-25:

- .NET 10 projects, MIT licensing, Git ignore rules, modular CLI commands, and local
  tool packaging. `mframe status`, `health`, and `capabilities` query the running host.
- `mframed serve` on IPv4 loopback with TLS 1.3, operator mTLS, private CA and leaf keys,
  stable mainframe/kernel identity, and exclusive state ownership.
- SQLite WAL/FULL metadata with transactional schema 1-to-2 migration, revocation,
  grants, program revisions, host roots, execution records, and metadata backups.
- `mframe cluster renew` with the host stopped: renews unexpired, authorized certificates
  during their final 24 hours and preserves identity. Protected staging and a journal
  recover interrupted publication before the next host starts. Expired/revoked
  identities are rejected; network reenrollment is still pending.
- Administrator program registration/list/removal with versioned manifests,
  explicit executable/interpreter paths, compatibility reporting, unambiguous
  resolution, environment allowlists, and frozen registration revisions.
- Multiplexed RPC and streaming with reserved receive capacity, fair DATA scheduling,
  prioritized control frames, bounded credit, EOF/COMPLETE ordering, cancellation,
  heartbeat, ten-second connection drain, and bounded completed-exchange records.
- Windows native process launch with explicit inherited handles and Job Object
  membership established before execution. Pipe mode preserves separate byte-exact
  stdout/stderr; `mframe exec` preserves child arguments and returns the child exit code.
- Real Windows ConPTY sessions through `mframe exec --terminal` and `mframe connect`, with
  input, resize, Ctrl+C, Windows console EOF conventions, and console-mode restoration.
- Kernel-owned shell sessions and parsing for quoted arguments, `help`, `programs`,
  `pwd`, `cd`, and `exit`. Pipelines, expansion, redirection, and implicit OS-shell
  evaluation are not supported.
- Administrator-approved `/host/<name>` roots for shell working-directory navigation.
  Named roots are checked against operator grants and program manifests, and reparse
  points are rejected. They are not filesystem providers or shared mounts.
- `Mainframe.Sdk`: single-use, 30-second inherited-pipe bootstrap, trusted CA delivery,
  separate program TCP/TLS sessions, and typed kernel queries. Effective permissions
  are the intersection of operator grants and the frozen manifest. Local execution
  authorization lasts 60 seconds, renews while authorized, and cannot revive after
  expiry. Peer-signed delegation remains pending.
- Foreground cleanup on detected session loss, cancellation, or authorization loss;
  at most two seconds for graceful termination before terminating the Job Object.
  Unfinished records become `outcome_unknown` on restart, without automatic retry.

Validation: `dotnet test Mainframe.slnx -c Release` passed **113 tests**. Final
streaming/disposal checks passed **10 targeted tests**. Release build completed
with zero warnings/errors, and CLI tool packaging succeeded. Tests exercise real
Windows processes, TLS, ConPTY input/resize/Ctrl+C/EOF, exact argv and stream bytes,
SDK scope/replay/expiry, lease expiry, grant removal, root navigation, frozen
revisions, process-tree cleanup, migrations, renewal recovery, backpressure,
protocol violations, and golden wire fixtures. The actual CLI shell acceptance
navigates `/host/workspace`, runs the standalone Mainframe.Hello SDK example, returns to the prompt,
and restores console modes on exit. Fixtures use synthetic data only.

This remains a local development host running as the current Windows account.
The initial operator has a persisted wildcard grant; launched programs are trusted
host processes, not sandboxed code. A dedicated service-account installer,
multi-user grant administration, network invitations/automatic certificate renewal,
public endpoint hardening, peers, Linux PTYs, host-directory/remote providers, background jobs,
and Windows filesystem export remain pending. No public listener or firewall rule
is installed. Linux support is not claimed until tested on the second machine.

Local limits: 32 connections, eight TLS handshakes, 64 foreground processes,
16 shells per connection, 128 programs and host roots, and 4 KiB serialized
manifests. Wire limits and the 4,096 active/unretired exchange-record bound (lifetime for legacy peers) are specified
in [packets.md](packets.md). Ordinary CLI RPC and launch acceptance deadlines are
ten seconds; accepted program runtime has no implicit ten-second timeout.

The remaining v1 target includes Windows/Linux and public endpoints, trusted
users/programs, one persistent coordinator, full terminal sessions, both storage
providers, and registered installed programs. Automatic failover, deployment,
hostile-code sandboxing, interactive reattachment, and Orleans remain outside v1.

## Layers and project boundaries

```text
mframe terminal client -- TCP/TLS --> Host-side shell and process execution
                                       |
                              Ordinary program process
                                       |
                           Language SDK -- TCP/TLS
                                       |
Windows filesystem adapter --> Kernel API and syscall dispatcher
                                  /                  \
                        Local handler          Remote kernel
```

Current and proposed projects:

| Project | Responsibility |
| --- | --- |
| Mainframe.Cli | Local connection options, terminal I/O, one-shot execution |
| Mainframe.Protocol | Versioned wire contracts, framing, streaming, and errors |
| Mainframe.Core | Dispatch, namespace, authorization, module contracts |
| Mainframe.Host | Persistent kernel (`mframed`), host-side shell, process supervision, endpoints |
| Mainframe.Client | .NET session/RPC client, authentication, streaming |
| Mainframe.Sdk | Process bootstrap and typed program kernel calls |
| Future adapter project | Windows filesystem integration |

The planned CLI is a thin remote terminal. The host-side shell parses mainframe
commands and starts programs; programs call typed kernel contracts through an SDK.
For example, the `cat` program performs open/read/close operations. The Windows
adapter calls the filesystem API directly; it must not launch CLI subprocesses
and parse terminal output. Keep the core independent of Windows integration.

## Remote shell and ordinary programs

Use TCP with TLS for all client/kernel and kernel/kernel connections, including
loopback connections. There is no separate named-pipe transport in the initial
design. The client selects an endpoint; it need not know the cluster topology.

Proposed interaction:

```text
mframe --endpoint server:7443 connect
atlas> weather
atlas> bank
atlas> cat /vol/documents/report.txt

# One-shot execution from the host OS shell:
mframe --endpoint server:7443 exec cat /vol/documents/report.txt
```

`mframe connect` opens an interactive remote shell. The mainframe shell owns the
logical working directory, environment, and command registry. One-shot execution
forwards arguments, streams, and the program's exit code. The shell supports quoted
arguments, logical working-directory changes, and registered commands. Pipelines,
redirection, expansion, and implicit host-shell evaluation are outside v1.

Programs are ordinary OS processes written in .NET or another language. A program
manifest declares its command name, launch information, OS/architecture/runtime
requirements, and required capabilities. The selected host starts the process.
The .NET runtime or a compatible self-contained build is still required for .NET
programs; native programs must match the execution host.

Keep two independent communication paths:

- Execution sessions relay stdin, stdout, stderr, input closure, cancellation,
  process exit status, and terminal metadata such as resize events.
- A separate program-to-kernel socket carries syscalls. Printing output or reading
  input must never interfere with RPC framing.

The same transport and framing can support terminal, program RPC, peer, and
filesystem client sessions, with different permissions and message contracts.
Scripted execution preserves separate stdout/stderr streams and exact bytes.
Implement Windows ConPTY and Linux PTYs through platform-specific launch adapters.
Interactive sessions use one terminal output stream. Forward dimensions, resize,
EOF, interrupts, and cancellation; restore client terminal settings on every exit.
Use Windows Job Objects and Linux process groups for supervised cleanup. Escaping
those groups is outside the trusted-program guarantee.

The host supplies the kernel endpoint, logical working directory, mainframe and
execution identities, arguments, and scoped short-lived authentication context.
Pass non-secret configuration through environment variables. Deliver a single-use
execution bootstrap credential through an explicitly inherited anonymous pipe/file
descriptor separate from stdin. Environment variables carry the endpoint and handle
identifier, not credentials. The credential expires after 30 seconds and creates a
process-scoped RPC session; renewal requires a live authorized execution. Programs
normally call their local kernel over their own TCP/TLS connection.

Define language-neutral, versioned contracts using portable types rather than
.NET object serialization. A .NET SDK can expose ordinary async methods and
streams, with equivalent libraries or generated clients for other languages.

Ordinary OS file APIs still access the host filesystem. Programs use the mainframe
SDK for its namespace, or an OS-mounted mainframe filesystem once available.
Kernel RPC permissions do not sandbox an OS process: start with trusted programs
and add execution isolation separately.

On detected foreground-session loss, initiate cleanup immediately with no reconnect
grace period. Allow at most two seconds for graceful termination, then force
termination and revoke execution credentials. Silent failures take up to the
connection timeout to detect. Cancellation across a partition is best effort, not
proof that a remote process stopped. Explicit background jobs survive terminal
disconnection; reconnectable interactive sessions are outside v1.

## Kernel capabilities and modules

Every kernel provides a small mandatory core:

- `kernel.describe`: stable node identity, logical mainframe identity, protocol version.
- `kernel.capabilities`: supported syscall contracts and resource scopes.
- `kernel.connect`: authorized peer connection management.
- `kernel.health`: readiness and subsystem health.

Modules register optional syscall families, such as `fs.*`, `jobs.*`,
`snapshots.*`, `gpu.*`, `sensors.*`, and `devices.*`.

Each capability describes its contract version, input/output types, supported
features, required permissions, and resource scope. Supporting `fs.read` does not
mean a kernel holds every volume. Capability advertisements never grant access.
Centralize namespace routing and permission enforcement in the core. Load trusted
.NET modules from administrator-configured paths at startup, with explicit method
contracts and handlers. Module changes require a host restart; no hot unloading.
Separate worker processes for module isolation can be considered later.

Storage providers declare features such as random access, writes, atomic rename,
snapshots, and change notifications. Do not pretend that a sensor stream supports
the same semantics as a persistent file.

## Peer connections and syscall protocol

See [packets.md](packets.md) for the selected 20-byte frame header, message types,
TLS handshake, RPC lifecycle, process/file streams, flow control, and peer relay.
It specifies the v1 target and identifies the implemented Windows-local execution subset separately.

Use explicit addresses on private or public networks and persistent authenticated
TCP/TLS 1.3 connections, including loopback. Implement custom framed RPC using
`SslStream`, `System.Text.Json` control messages, and raw DATA frames. No gRPC,
named-pipe network transport, NAT traversal, or automatic public relay is included.
QUIC is deferred; keep application contracts independent of transport details.
Exchange identity, compatible versions, and capability manifests during connection
setup; invalidate unavailable providers when peers disconnect.

Joining requires a single-use administrator-issued invitation; possession is
sufficient approval. Discovery alone does not authorize membership. Authenticate
callers and authorize each operation. Preserve validated delegation across peer
forwarding; never trust a client-supplied identity field as authority.

Requests carry a request ID, syscall name/version, target resource, authenticated
caller context, deadline, and typed arguments. Responses carry the request ID and
a typed result or structured error. Support bounded messages, streaming,
backpressure, cancellation, and limits on concurrent work.

Distinguish unsupported contracts, denied access, unavailable resources, expired
deadlines, and unknown operation outcomes. A lost response does not prove the
operation did not execute. Retry only operations with suitable idempotency or
deduplication rules. Bound forwarding to prevent routing loops.

### Versioning and limits

Publish JSON schemas and wire fixtures alongside the implementation. Encode file
sizes and offsets as decimal strings; bounded values such as terminal dimensions
use JSON numbers. Do not use reflection-based object serialization or arbitrary
type activation. Negotiate the highest mutually supported protocol major and
optional features explicitly. Breaking method changes require a new method version.
Within a major, accept additive optional fields and ignore unknown JSON fields,
but reject duplicate properties, missing required fields, unsupported required
features, and unknown frame types.

| Setting | Default |
| --- | ---: |
| Frame payload | 1 MiB maximum |
| DATA chunk | 64 KiB |
| JSON nesting | 32 levels |
| Concurrent exchanges per connection | 128 |
| Channels per exchange | 8 |
| Outstanding credit per channel | 256 KiB maximum |
| Aggregate inbound/outbound buffering per connection | 16 MiB |
| TLS/application handshake deadline | 10 seconds per stage |
| Idle heartbeat interval | 10 seconds |
| Unresponsive connection timeout | 30 seconds |
| Graceful shutdown drain | 10 seconds |

Grant stream credit only when capacity exists. Schedule DATA fairly and prioritize
control traffic. Preserve bounded terminal-exchange records to drain already-
authorized late DATA without delivery, reject credit overruns, and never reuse
exchange IDs. Close a connection rather than exceed bookkeeping limits.

## Enrollment, identities, and permissions

`cluster init` creates mainframe/coordinator identities and a private cluster CA.
Store private keys under the dedicated service account with restrictive filesystem
permissions. Exclude keys, invitations, and authentication material from logs and
backups intended for public sharing.

Nodes and operator devices generate their own keys and submit certificate requests.
Administrators issue cryptographically random invitations valid for 15 minutes,
specifying identity and role, coordinator address, and trusted CA fingerprint.
Verify that fingerprint before presenting the invitation. Consume invitations
transactionally; retries using the same public key return the original enrollment
result rather than another identity. There is no second approval step.

Issue seven-day certificates; renew in the final 24 hours while authorized. Expired
or revoked identities require reenrollment. Use mutual TLS for enrolled nodes and
operator devices. Connections without a client certificate are limited to
enrollment or the explicit process-bootstrap authentication path; they cannot make
ordinary operational calls before authentication. Certificate identities do not
replace current authorization grants. Revocation prevents new leases and triggers
connected-session termination.

Public endpoints require configurable per-address/global connection and handshake
limits, authentication throttling, bounded enrollment requests, and security event
logging. Never fall back to plaintext or accept arbitrary certificates.

Grant named users program execution, volume read/write, device access, and
administration permissions. Programs receive the intersection of user grants and
manifest permissions. Use coordinator-signed, audience-bound authorization leases
lasting 60 seconds. Validate signature, recipient, execution identity, scope, and
expiry. Forwarding can narrow but never expand permissions. Require synchronized
clocks with at most five seconds of skew.

## Routing and the single-machine experience

Normal operations name logical resources, not their physical host:

```text
atlas> ls /vol/projects
atlas> cat /vol/projects/readme.txt
atlas> jobs
atlas> services
```

Keep three logical directories with distinct responsibilities:

| Directory | Purpose |
| --- | --- |
| Programs | Locate hosts able to execute a registered command |
| Resources | Resolve volume paths and devices to their authoritative owners |
| Capabilities | Discover syscall contracts and features provided by each kernel |

Program execution placement and syscall routing are independent decisions.
The capability registry associates providers with resource scopes and readiness:

```text
fs.read       storage-01    volume:archive    ready
fs.write      storage-01    volume:archive    ready
jobs.submit   desktop-01    executor:general  ready
sensors.read  workshop-01   device:ambient    ready
```

Resolve file/device requests by resource ownership. Schedule compute work by
capability and available capacity. Never route writes to an arbitrary provider
solely because it supports the syscall name. Support explicit targeting when an
operator needs a particular node.

The dispatcher selects a local handler or remote connection. Open file handles
retain owning node, session identity, and execution generation. Define close, timeout, and node-restart
behavior so stale handles cannot accidentally reference new resources.

Administrative commands expose physical topology: `mframe nodes`, `mframe node inspect`,
`mframe volume inspect`, and `mframe job inspect`. One logical machine should not hide
failures or make diagnosis difficult.

### Two-machine example

Machine A hosts `weather` and `cat`; machine B hosts `bank` and the storage provider
for `/vol/documents`. An authenticated terminal connected to either kernel can
execute any registered program for which its user has permission.

When connected to A, executing `bank` uses the program directory to select B:

```text
Terminal -> Kernel A -> Kernel B -> bank process
         <-          <-          <- stdout/stderr and exit status
```

Input and cancellation travel toward the process. Initially, relay session I/O
through the entry kernel rather than requiring direct client connections to every
execution host. Optimized routes can be added later.

Executing `cat /vol/documents/report.txt` can run `cat` on A while serving file
operations on B:

```text
cat on A -> Kernel A -> Kernel B -> Storage provider
           fs.open      owns /vol/documents

Storage -> Kernel B -> Kernel A -> cat -> stdout -> Terminal
```

Open returns an opaque handle whose reads, seeks, and close calls route to B.
User identity and delegated permissions follow both execution and RPC calls.
The `/vol` listing comes from the shared mount namespace; `/vol/documents`
enumeration is delegated to B. Never choose an arbitrary filesystem provider
solely because it implements `fs.open` or `fs.list`.

Acceptance scenario: connect to either kernel, run `weather` on A and `bank` on B,
and run `cat` on A against a file on B with correct content, stream routing, and
exit status. Use fixture programs and synthetic account data for these tests;
this example does not require a real banking integration. Also verify permission
denials, owner disconnection, cancellation, and stale handle rejection.

## Namespace and storage

Proposed namespace:

```text
/sys         Kernel status, configuration, module inventory
/vol         Persistent volumes
/dev         Devices and telemetry
/proc        Running mainframe jobs
/services    Managed services and status
/logs        System and subsystem logs
/snapshots   Historical volume views
/net         Explicit remote-node views where useful
```

Entries may be stored files or generated views. Normal file reads must not trigger
control actions: Explorer, indexers, and antivirus can read files automatically.
Use explicit syscalls for job submission and device control. Start Windows exports
with persistent files and stable status documents; define stream behavior separately.

Distinguish internal provider mounts from Windows exports:

```text
atlas> mount archive /vol/archive
atlas> export / --drive M:
atlas> export /vol/archive --directory C:\Mainframe\Archive
```

Start with one authoritative local owner per volume. Remote access follows in the
linked-kernel milestone; it is not required for initial encrypted storage.
If the owner disconnects, report the volume as unavailable. A shared namespace
does not imply replicated storage. Later replication policies must specify which
copies acknowledge a write, when success means durable storage, and how recovery
and degraded operation work. Do not silently downgrade durability.

### Providers and names

Implement encrypted managed volumes first, locally on Windows:

- Managed volume: one password-unlocked SQLCipher container (`.mfv`) per volume,
  with encrypted directories, metadata, and 64 KiB file-content chunks. Mount it
  under `/vol/<name>`. See the detailed next-milestone plan below.
- Host-directory volume: a later v1 provider exposing an administrator-selected
  existing directory through the same filesystem API. It is outside the next
  milestone; current `/host` navigation does not implement this provider.

Paths use `/`. Normalize managed names to Unicode NFC, preserve spelling, and
compare server-side with ordinal case-insensitive comparison. Reject Windows-
reserved names, control characters, invalid Windows filename characters, trailing
spaces/dots, and case collisions. Limit names to 255 UTF-16 code units and full
logical paths to 4,096. SDKs defer name resolution to the server.

The host-directory provider applies the same exposed naming rules. Reject an
incompatible existing tree at mount validation; later external collisions or
incompatible names fail explicitly without renaming/deleting host data. Do not
traverse symbolic links, junctions, or reparse points in v1. Use stable volume IDs
and opaque owner/session-generation handles; handles do not survive kernel restart.

### Consistency and durability

The authoritative owner serializes operations for its volume. Managed operations
are transactional; reads see committed data and each bounded write commits
atomically. Multi-request uploads are not a single transaction. Provide temporary
files and atomic same-volume rename/replace for complete-file publication. Reject
cross-volume rename. Delete/rename metadata transactionally and reclaim unreachable
chunks in bounded maintenance transactions.

Support access modes and explicit read/write/delete sharing flags enforced at the
owner. Version-2 handles support shared/exclusive byte-range locks; version-1
clients participate in enforcement but cannot acquire them. Do not cache
file contents/metadata between client RPC calls. Enumerate directories in bounded
batches; concurrent changes can affect subsequent batches.

Managed writes acknowledge after SQLite commit. Host-directory writes acknowledge
OS acceptance; `fs.flush` explicitly requests durable flushing. Expose these
provider capabilities. Close is not a substitute for flush. Disconnects may leave
partial writes; never automatically replay mutations.

Host-directory consistency is limited by direct external modification. Mainframe
locks coordinate its clients, not arbitrary host applications. Detect changed or
vanished resources and report errors. Future filesystem adapters must respect
these provider limits rather than claim stronger semantics.

## Local encrypted volumes

Status: implemented for Windows x64. This supersedes the former requirement to ship both providers together.
Linux, peers, host-directory providers, and WinFsp remain separate milestones.

The implementation uses source-built SQLCipher 4.19.0 with OpenSSL 3.5.8, pinned
archives/revisions and MSVC 14.44.35207 in `native/dependencies.json`. Build with
`native/build.ps1`; source archives, portable build tools, and binaries remain in
ignored `artifacts/native`. Runtime notices are copied with build/publish output.
Existing unencrypted coordinator databases use the same qualified engine and
migrate to schema 3 without changing identities or registrations.

Implemented interfaces include volume administration, all filesystem calls below,
`MainframeProgram.Files`, seekable SDK streams, and standalone Ls/Cat/StorageDemo
programs. Storage transfer completions contain `bytes` and `eof`; they are not
process exit statuses. Four storage operations per connection reserve 256 KiB each
within the transport budget. Each volume has its own serialized owner gate and
64-operation admission limit. Independent volumes can run concurrently. Unmount
refuses while handles or operations remain on that volume.

Chunks invalidated by delete/truncate are reclaimed in batches of at most 128.
Truncation records an epoch boundary so later extension cannot reveal old bytes.
Maintenance runs one volume per tick and retains recovery data if it fails.
Password operations have a separate two-operation admission limit and throttling.
No build or acceptance step modifies the user's live mounts or registrations.

### Container and encryption decisions

Use SQLCipher Community Edition through a compatible native SQLite binding. Keep
`Microsoft.Data.Sqlite` where the binding supports it; do not implement a custom
cipher or filesystem journal. The first task pins a supported release, crypto
backend, native Windows x64 binary/build recipe, and redistribution notices.
Verify that native dependency resolution cannot silently select ordinary SQLite.
If a suitable distribution cannot be validated, report that blocker rather than
substitute plaintext storage. Test the existing coordinator database with the
selected binding; volume encryption must not change its identity or schema.

A `.mfv` file is an encrypted SQLite database containing a volume UUID, numbered
format/schema version, directory entries, timestamps, lengths, and 64 KiB content
chunks. Filenames and file metadata are encrypted as well as content. Use the
selected stable SQLCipher release's authenticated page encryption and password
KDF defaults, pinned as a documented format profile. No plaintext SQLite header,
custom password hashing, automatic legacy-format probing, or plaintext fallback.
Reject unsupported formats; migrations are numbered, transactional, and explicit.

Use WAL, foreign keys, `synchronous=FULL`, and memory-only temporary stores. Verify
these settings on each connection. Disable connection pooling for keyed databases
so unmount closes every keyed connection. Data pages in recovery files must also
be encrypted. WAL/SHM headers and file sizes may expose structural information.
Container size, host path, mount name, and volume UUID are not secrets.

Allow `.mfv-wal` and `.mfv-shm` while mounted or after a crash. Preserve them for
recovery; never delete them as a cleanup shortcut. A successful clean unmount
checkpoints and closes the database, leaving a portable `.mfv` file. If checkpoint
or flush fails, report it and preserve recovery files. Copying a live `.mfv` alone
is not a backup. Initially support copying only after successful clean unmount;
live backup/export tooling is later work. Durability assumes the OS and device
honor flushes; process-kill tests do not establish power-loss hardware behavior.

### Passwords and mount lifecycle

Administrator commands:

```powershell
mframe volume create E:\Mainframe\Data\documents.mfv
mframe volume mount E:\Mainframe\Data\documents.mfv /vol/documents
mframe volume list
mframe volume unmount /vol/documents
```

`create` prompts twice, refuses an existing destination, initializes and validates
a container, closes it cleanly, and leaves it unmounted. Never overwrite existing
files or publish a partially initialized container as complete. Use protected
same-directory staging and exclusive publication; interruptions leave an explicit
recoverable staging artifact, not a mounted volume. Creation RPCs are not replayed.

`mount` prompts once, authenticates the local operator, opens an existing file
without create-if-missing behavior, verifies its key/schema/integrity, then
publishes `/vol/<name>` atomically. Mount names are one valid normalized component;
no nested or overlapping mounts in this milestone. Reserve `/vol` as a virtual
root. Reject duplicate mount names, duplicate volume UUIDs, and alternate paths to
an already-open container. Use canonical host-file identity and exclusive ownership
for the container lifetime; reject network paths and reparse-point paths. Bound
integrity validation time and fail without exposing a half-mounted namespace.

Read passwords from a masked console prompt, never argv, environment variables,
terminal process streams, config files, logs, or coordinator metadata. Require a
usable console for create/mount in this milestone. There is no password-specific
length or character policy, including for empty passwords. Passwords are
case-sensitive and never normalized or trimmed. The existing RPC frame-size bound
still applies to create/mount requests. Passwords travel
only in sensitive operator RPC payloads over the existing authenticated TLS link;
programs cannot invoke mount/unlock administration or receive passwords. Redact
these requests before tracing, exception formatting, or audit logging.

Pass password bytes through SQLCipher's length-aware `sqlite3_key` API, not SQL
text or a connection-string password. Existing nonempty passwords without NUL
retain their UTF-8 bytes. Empty or NUL-containing passwords use one NUL byte
followed by base64 of their UTF-8 bytes. This disjoint encoding supplies nonempty
key material, preserves embedded NULs, and keeps existing containers compatible.
SQLCipher still performs its standard password derivation. An empty password
must never select plaintext mode.

Keep key material only for the unlocked lifetime, minimize password copies, clear
mutable buffers, and release native keys on close. Managed strings, OS paging, and
crash dumps prevent a promise of complete memory erasure. Encryption protects data
at rest; the kernel and trusted host processes can see plaintext while unlocked.
There is no password recovery, saved key, automatic unlock, password change, or
rekey operation in this milestone. Tell the operator this when creating a volume.

Persist mount configuration (UUID, path, mount name), never its password. Host
restart returns configured mounts as `locked`; manual `volume mount` unlocks the
matching entry. Missing files remain unavailable without being recreated. Explicit
successful unmount removes the configured mount, not the container or grants.
Unmount rejects new opens while draining, returns `VOLUME_BUSY` if handles or I/O
are active, and does not silently invalidate them. No force-unmount command yet.
A mounted volume outlives the operator connection that unlocked it. Ownership is
local; no discovery, replication, or remote delegation is needed yet.

### Filesystem and SDK contracts

Add a storage project with a provider interface and an encrypted implementation;
keep namespace resolution and authorization in the kernel. Extend protocol contracts,
source-generated JSON, client APIs, SDK, and capability discovery together.

- Administration: `volume.create`, `volume.mount`, `volume.list`, `volume.unmount`.
- Metadata: `fs.list`, `fs.stat`, `fs.mkdir`, `fs.delete`, `fs.rename`.
- Handles: `fs.open`, `fs.read`, `fs.write`, `fs.truncate`, `fs.flush`, `fs.close`.
- SDK: async directory/file APIs and a seekable `Stream` adapter using explicit
  offsets, with async disposal, cancellation, and no mutation retries. Serialize
  operations that change a stream's position. Limit lengths/offsets to nonnegative
  signed 64-bit values and check overflow. Seeking past EOF is allowed; subsequent
  writes/truncation extend with zero-filled logical gaps without allocating all
  intervening chunks. Read beyond EOF returns zero bytes.

The existing naming policy still applies. Resolve NFC names using a registered
server-side ordinal-ignore-case SQLite collation, not SQLite's ASCII-only NOCASE.
Enforce uniqueness transactionally. Reject traversal above `/`, Windows-reserved
names, invalid characters, trailing dots/spaces, and case collisions. Do not store
symlinks, hard links, alternate data streams, or executable host paths in volumes.
Programs continue launching from existing host working directories; virtual paths
are SDK arguments, not OS working directories. `/host` shell navigation is unchanged.

Issue opaque handles bound to connection, execution/operator, volume ID, and mount
and host generations. Validate read/write access and sharing flags on every call.
Close handles on connection/execution loss or lease expiry. Separate SDK connections
cannot transfer handles. The process cannot use a handle to widen its permissions.
Require `volume.manage` for operator administration; programs are always excluded
from these methods. Filesystem grants use `volume:<uuid>:read` and
`volume:<uuid>:write`, intersected with the frozen manifest and current operator
grants. Reads/list/stat require read; mutation requires write; mixed access requires
both. Check lease/grant validity at dispatch and immediately before write commit.
Extend manifest validation to these resource scopes; an `fs.*` method name alone
must not authorize every volume. Preserve existing wildcard operator behavior.

### Atomicity, bounds, and errors

Use one serialized operation queue per volume. Buffer a complete bounded write
before beginning its transaction; never keep a write transaction waiting for the
network. Each write accepts at most 64 KiB and commits data, length, and timestamps
together. SDK larger writes split into requests. Earlier committed requests remain
if a later request fails. Cancellation before commit rolls back; cancellation or
connection loss racing a commit may produce an unknown outcome. Never retry it.

Open supports existing/create-new/open-or-create/truncate modes, access, and explicit
read/write/delete sharing flags. Creating with create-new fails if the name exists.
Publishing a complete file uses a caller-chosen temporary name with create-new,
bounded writes, flush, and atomic same-volume rename with explicit replace control.
Check sharing restrictions for source and destination. Crash before publication
leaves the old target and possibly a temporary file; crash after commit leaves the
new target. Never infer abandoned files solely from their names. Delete requires
no conflicting handles; directory deletion is empty-only. Reclaim unreachable chunks
in bounded transactions without exposing deleted contents through reused entries.

Managed mutations acknowledge after FULL commit; `fs.flush` confirms prior writes
and reports underlying errors, without promising a checkpoint or backup-ready file.
Close does not replace flush. Enumeration is bounded and may observe changes between
batches; it is not a snapshot. Version-2 handles support byte-range locks;
cross-volume rename returns `NOT_SUPPORTED`. Preserve the 16 MiB connection transport budget and reserve read/
write buffers within it rather than introducing an unbounded storage queue.

Initial configurable caps: 32 mounted volumes, 256 handles per connection, 1,024
handles per host, 256 entries per enumeration batch, and 64 queued operations per
volume. Bound enumeration responses by the existing frame limit, even below 256
entries. Permit two concurrent password/KDF operations per host; throttle failed
unlocks per operator (five per minute) and globally (twenty per minute). Reject
excess work with a retryable busy error, without retaining password-bearing queues.

Specify stable errors for locked/unavailable/busy volumes, authentication failure,
access denied, sharing violations, missing/existing entries, invalid paths, invalid
handles, disk full, corruption, unsupported format, and I/O failure. Wrong passwords
and unreadable encrypted headers return a generic unlock failure, not a guessed
password-versus-corruption diagnosis. Acknowledged durability, transport failure,
and unknown mutation outcomes remain distinct.

### Implementation sequence and acceptance

1. Validate and pin the SQLCipher binding/native package and license notices. Prove
   encrypted main/WAL data, memory-only temp storage, rejection by plain SQLite,
   wrong-password failure, and coexistence with existing metadata databases.
2. Implement the versioned container schema, local ownership, create/mount/unmount,
   locked restart records, prompts, throttling, redaction, and mount permissions.
3. Implement names, chunks, transactions, handle/sharing tables, bounds, cleanup,
   durability errors, and atomic publication; test the provider without transport.
4. Add the versioned storage RPCs and SDK stream APIs using existing flow control.
   Add schemas and golden transcripts, including commit/COMPLETE cancellation races.
5. Add standalone SDK examples `Mainframe.Ls`, `Mainframe.Cat`, and
   `Mainframe.StorageDemo`; register them explicitly with volume-scoped permissions.
   Keep their entry points as `Program.Main` and apply the repository editorconfig.
6. Run isolated Windows acceptance and document actual results before marking done.

Acceptance creates and mounts a password-protected volume, writes nested files
through the SDK, verifies binary-exact reads through `cat`, enumerates with `ls`,
cleanly unmounts, remounts, and compares content and metadata. Test default/empty
files, seek/truncate/zero gaps, case collisions, traversal, sharing conflicts,
permissions, lease expiry, disconnected clients, memory bounds, and stale handles.

Use deterministic failure injection and real child-host termination before/during/
after commits, rename publication, checkpoint, and initial creation. On restart,
volumes are locked. Correct-password mounting recovers acknowledged commits and
never exposes torn bounded writes. Simulate disk-full and flush failures, corrupt
pages, missing recovery files, duplicate UUIDs, competing owners, and unsupported
schemas. Validate copy-after-clean-unmount on a new local state directory, with
fresh explicit grants. Search artifacts/logs for known content, filename, and
password sentinels; this complements native encryption checks, not a cryptographic
proof. Keep all existing execution/identity/protocol tests passing. No Linux or
WinFsp acceptance claim is part of this work.

Implementation references: [SQLCipher design](https://www.zetetic.net/sqlcipher/design/),
[SQLCipher licensing](https://www.zetetic.net/sqlcipher/license/), and
[Microsoft.Data.Sqlite encryption](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/encryption).
The selected native distribution and its dependencies need their own retained
notices; Mainframe remains MIT. The source-built encryption dependency is now used; notices are retained under `native`.

## Coordination and failures

Peer discovery and capability exchange do not establish resource ownership or
resolve conflicting writes. Keep coordination separate from syscall transport.

One designated coordinator owns shared namespace metadata, membership, grants,
program registrations, and scheduling. Any node may also act as gateway, storage
provider, or worker. No automatic promotion or owner reassignment is included.

Use `Microsoft.Data.Sqlite`, WAL, foreign keys, and `synchronous=FULL`. Keep databases
on local disks, never network shares. The coordinator stores identities,
revocations, grants, mounts, registrations, durable job records, and schema versions.
Each node stores execution/recovery state locally; managed volumes use separate
local databases. Apply numbered transactional migrations, reject unsupported newer
schemas, and provide consistent backups through SQLite's backup API.

Coordinator outage blocks new sessions, starts, opens, membership changes, and
grant changes. Existing processes and handles may continue until their current
authorization leases expire; then deny protected operations and initiate cleanup.
Recovery restores the designated coordinator and reconciles node state. No Orleans
dependency: explicit routing, coordination, and process supervision cover v1.

Later, evaluate an established consensus implementation for three voting
coordinators. A majority can make authoritative changes; a minority must stop
them. Prevent stale owners/workers from committing operations after reassignment,
using ownership generations or equivalent fencing checked at the resource.
Metadata consensus does not itself replicate file contents or recover running jobs.

Do not promise that two machines can always make independent progress during a
network split while maintaining one consistent writable system.

## Jobs and services

### Registration and execution

Administrators register already-installed programs; there is no bundle upload,
download, or deployment in v1. Versioned JSON manifests declare identity, semantic
version, absolute executable path, fixed arguments, host working directory,
OS/architecture/runtime requirements, I/O modes, environment allowlist, and requested
permissions. Keep host working directories distinct from the logical shell directory.
Support native executables, .NET applications, and scripts through a registered
interpreter. Never concatenate untrusted arguments into an OS shell command.

Registrations belong to nodes. Incompatible ones remain visible but cannot run.
Short names resolve only with exactly one eligible registration; otherwise return
candidates and require `namespace/name@version#node`. Freeze the registration
revision per execution and retain it in job records. Changes affect future starts.

Run trusted programs under a dedicated non-administrator service account. RPC
grants are not a hostile-code sandbox. After a host crash, unresolved jobs become
`interrupted` or `outcome_unknown`; do not retry automatically.

Provide durable job IDs, a job spool, progress, logs, cancellation, and explicit
retry policies. Explicit background jobs should survive a CLI disconnect;
ordinary foreground sessions follow the cancellation policy above. Placement can consider
memory, GPU capability, installed tools, data locality, and attached devices.

```text
atlas> run index-documents --volume projects
atlas> run render-scene --requires gpu
atlas> job logs job-0042 --follow
```

An individual program runs on one host unless designed to split its work. RAM and
CPU are not transparently pooled, and live process migration is outside the initial
scope. A disconnected worker may still be executing; define duplicate-effect and
ownership rules before enabling automatic reassignment.

## Operator experience and later modules

- Boot profiles select modules, mounts, services, and startup jobs.
- Subsystem health reports online, degraded, offline, or recovering with reasons.
- Operator identities and permissions control resources and commands.
- An event journal records administrative actions and lifecycle transitions.
- Snapshots expose historical read-only volume views.
- Recovery mode starts a minimal maintenance environment.
- Enhance the remote shell with a station-style presentation after basic sessions work.
- Machine-readable output supports scripts independently of terminal styling.

These are later features; do not expand the first peer milestone to include them.

## Windows adapter and licensing

Mainframe remains MIT licensed. WinFsp is the leading candidate for a future
drive-letter or directory export, not a current dependency. Its GPLv3 FLOSS
exception permits qualifying open-source applications to link to its specified
DLLs under their own license, subject to its conditions. Include the required
attribution and repository link in the UI and user-facing documentation when
integrated. The exception prohibits linking or distributing with proprietary
software and allows redistribution of unmodified official installers. Review
the chosen release and any bindings before distribution.

ProjFS remains an alternative for a directory projection with locally cached
content. Select the adapter based on the required filesystem semantics.

## Implementation milestones and interfaces

Introduce typed clients for identity/capabilities, shell sessions, process execution,
file handles/streams, program registration, and enrollment. Keep syscall contracts
independent of presentation and storage implementation. The CLI, local kernel,
Windows execution, SDK, and interactive shell are implemented. Linux and distributed
behavior require their own implementation and acceptance tests.

1. **CLI foundation (implemented).** Keep commands modular; extend the argument
   contract as real operations are introduced.
2. **Protocol, identity, and coordinator (local subset implemented).** Framing,
   multiplexing/flow control, TLS 1.3, local operator authorization and stopped-host
   renewal, SQLite migrations, registrations, grants, and execution records exist.
   Next complete network enrollment/renewal, multi-user administration, signed
   delegation, public hardening, and distributed coordinator metadata.
3. **Program SDK and terminal execution (Windows implemented).** Scoped bootstrap,
   pipe execution, Windows ConPTY, Job Objects, cancellation, terminal restoration,
   registered programs, host-root navigation, and the kernel-owned shell are tested.
   Next implement the Linux launch/process-group/PTY adapters and validate on the
   second machine. Keep the same wire, SDK, and manifest contracts.
4. **Local encrypted storage (Windows implemented).** See implementation and
   acceptance details in [the encrypted-volume plan](#local-encrypted-volumes).
   Password-unlocked containers, local `/vol` mounts, SDK file access, and crash-safe
   writes come before peers. Host-directory providers and WinFsp are excluded.
5. **Virtual root and writable backend (Windows implementation).** `/`, `/vol`, directory handles, container schema 2, retained objects, metadata/allocation, deletion dispositions, range locks, filesystem-role mTLS, and negotiated exchange retirement. See the acceptance record below.
6. **Two linked kernels.** Establish authenticated peers, exchange manifests, and
   route a remote read-only syscall. Both CLIs show the same mainframe identity and
   both nodes. Verify incompatible versions, unauthorized peers, disconnection,
   deadlines, and reconnection without stale registry entries. Register programs
   per host and relay a remote process's I/O through either entry kernel.
7. **Storage expansion and shared namespace.** Add the host-directory provider
   alongside encrypted managed volumes, remote handle routing, and owner availability.
   Read/write through either node. Verify permissions,
   concurrent access, owner loss, and stale handles. Complete the two-machine
   weather/bank/cat fixture scenario, including `cat` on A reading storage on B.
8. **Distributed jobs.** Add durable submission, placement, logs, cancellation,
   and explicit failure/retry rules. Verify ambiguous outcomes and worker loss.
9. **Windows export (separate adapter milestone).** Implement the selected adapter against the same client API.
   Verify normal editor workflows and that CLI and Windows see the same data.
10. **Later work, outside v1.** Replication, coordinator failover, snapshots, program
   deployment, hostile-code sandboxing, QUIC, session reattachment, and advanced
   operator tools require separate plans and failure tests.

## Resolved v1 decisions

| Area | Decision |
| --- | --- |
| RPC | Custom 20-byte frames, TCP/TLS 1.3, JSON controls, binary streams |
| Enrollment | Single-use 15-minute invitations; no second approval |
| Operator authentication | Enrolled device keys and seven-day certificates |
| Permissions | Resource grants intersected with program manifest permissions |
| Coordination | One persistent coordinator; bounded lease-based outage behavior |
| Metadata | Local SQLite WAL/FULL; transactional migrations and backups |
| Storage | Local password-unlocked SQLCipher containers first; host-directory and remote access later in v1 |
| Names | NFC, case-preserving/case-insensitive, Windows-friendly names |
| Execution | Trusted local executables/interpreters; no sandbox claim |
| Registration | Admin manifests; ambiguous names require version/node qualification |
| Terminal | ConPTY and Linux PTYs; immediate cleanup on detected session loss |
| Bootstrap | Single-use 30-second credential through inherited pipe/descriptor |
| Orleans | Not used in v1 |

## Storage implementation validation

Provider, protocol, and real Windows integration tests cover encryption and WAL
sentinels, wrong passwords, unsupported/corrupt containers, sparse reads, truncation,
case collisions, rollback, bounded reclamation, simulated disk-full/flush failures,
sharing, read-only program scopes, cross-session handles and cursor rejection,
revocation, invalid/short/overlong writes, cancellation, and binary-exact SDK output.
Real child kernels are terminated before/after write commit and during creation
publication/checkpoint. Restart requires unlock and recovers committed content.
A real ConPTY test verifies masked password confirmation and console restoration.
On 2026-09-26, all 146 tests passed in both Debug and Release on Windows x64,
with no failures or skipped tests. The suite includes standalone SDK demo/ls/cat
acceptance, locked restart and unlock, and the existing execution/Hello tests.
It also verifies rejection by Windows' ordinary SQLite engine, plus independent
volume progress when another volume's queue is full. Password-policy follow-up
tests cover empty, short, long, Unicode, and control-character passwords through
the provider, RPCs, and real console prompts; empty-password containers stay encrypted.
After removing the password policy, all 156 tests passed in Debug on 2026-09-26.

Process termination and injected I/O failures do not establish physical power-loss
behavior. Durable acknowledgements rely on OS/device flush guarantees. Native DLL
builds and runtime acceptance currently cover Windows x64 only; Linux is not claimed.
Missing or externally discarded WAL files can destroy acknowledged data; recovery
files must be preserved. File-backed temporary stores and plaintext fallback are
prohibited. Corruption is reported, not automatically repaired or silently ignored.

## Validation and acceptance

- Protocol: fragmented/coalesced frames, malformed JSON, illegal transitions, ID
  collisions, slow readers, bounded memory, cancellation races, completion ordering.
- Identity: invitation expiry/replay, fingerprints, expired/revoked certificates,
  denied delegation, public-endpoint throttling, and bootstrap expiry/reuse.
- Coordinator: restart persistence, transactional enrollment, backup/restore,
  schema rejection, outage blocking, lease expiry, and recovery reconciliation.
- Local encrypted storage: wrong passwords, encrypted recovery files, locked restart,
  bounded SDK I/O, atomic publication, and crash recovery as specified below.
- Later storage: cross-node I/O, naming collisions, traversal/link rejection, sharing
  modes, partial writes, flush, atomic replacement, stale handles, and crash recovery.
- Execution: exact arguments, ambiguity, qualified names, scoped credentials,
  exit codes, cancellation, and interrupted-job recovery.
- Terminals: real Windows ConPTY and Linux PTY tests for interactive input, resize,
  Ctrl+C, EOF, process-tree cleanup, and client terminal restoration.
- End-to-end: connect through either node, execute fixture weather on A and bank
  on B, and cat on A against storage on B. Verify content, grants, streams, routing,
  and exit status. Use synthetic data, not real banking integration.

Require actual testing on both operating systems before claiming cross-platform
support. Passing document checks does not establish runtime acceptance.

## References

- [WinFsp tutorial and mount points](https://winfsp.dev/doc/WinFsp-Tutorial/)
- [WinFsp license and FLOSS exception](https://github.com/winfsp/winfsp/blob/master/License.txt)
- [Microsoft ProjFS provider overview](https://learn.microsoft.com/en-us/windows/win32/projfs/provider-overview)
- [etcd failure behavior](https://etcd.io/docs/v3.7/op-guide/failures/)
- [Orleans overview](https://dotnet.github.io/orleans/docs/overview/)


## Virtual root and writable backend: September 2026

This milestone prepares the kernel for a writable Windows adapter. It does not
install WinFsp, export a drive, or make `/vol` a Windows working directory.
`mframe connect` retains `/host` navigation; SDK filesystem paths remain absolute.

The namespace resolver accepts `/`, `/vol`, and configured mount names. It collapses
repeated separators and dot components, permits traversal back to `/`, and rejects
traversal above it. `vol` is case-insensitive and displayed canonically. Root and
`/vol` have stable namespace identities and Unix-epoch timestamps. Mounted roots
have the container's stable identity and timestamps. Authorized locked mounts stay
visible with `state: locked`; contents require manual unlock. Listings filter on
volume read grants. Ordinary mutations cannot change namespace or mount nodes.
`/host`, `/sys`, `/proc`, and `/dev` are not filesystem RPC providers.

Container schema 2 migrates schema 1 transactionally, preserving UUID, entry IDs,
names, bytes, and original timestamps. Access/change times initially equal the old
modification time; allocation initially equals EOF. Names can be detached while
version-2 handles retain the underlying object. Deletion intent is durable and can
be cleared before cleanup. Cleanup releases sharing reservations and range locks;
final close releases object retention. Restart completes durable deletion intents.
Maintenance reclaims unreferenced objects and chunks in batches of at most 128.
The coordinator database, password handling, and SQLCipher settings are unchanged.

Version-2 opens have explicit rights and creation dispositions. Rights map to the
existing volume read/write grants and cannot expand caller authority. Metadata-only
opens and directory handles are supported. Enumeration is ordered by ordinal
case-insensitive name, uses at most 256 rows, and supports restart, an initial name
marker, and a signed handle/generation-bound continuation. It is not a snapshot.

Metadata includes a volume UUID plus stable entry ID, four timestamps, supported
attributes, logical allocation, and stored chunk bytes. Read-only files reject data
mutation and deletion; an authorized metadata update can clear the flag. Allocation
is thin provisioning, not reserved disk space. Shared host backing capacity is
reported separately and deduplicated in namespace queries. File and volume flush
failures propagate. No hardware power-loss guarantee is inferred from OS success.

Writes remain bounded to 64 KiB and commit before COMPLETE. Atomic append chooses
EOF in the owner transaction; constrained writes report the bytes committed without
extending EOF. Shared/exclusive byte-range locks are handle-owned and fail fast,
with 256 per handle and 4,096 per host. SDK and legacy calls enforce the same locks.
Version-1 handles retain their invalidation-on-delete/replacement behavior.

An authenticated `filesystem` connection uses the operator certificate but permits
only filesystem RPCs and kernel discovery. Volume administration, program execution,
and enrollment are denied. Trust is validated at TLS session establishment;
certificate expiry, persisted revocation, and current grants remain checked during
operation dispatch and before mutation commit.

`namespace-v1`, `storage-v2`, and `exchange-retire-v1` are negotiated. RETIRE and
RETIRE_ACK fence exchange traffic before releasing completed records. IDs increase
and are never reused. Active plus unretired records stay bounded at 4,096; legacy
connections keep their lifetime bound. No mutation is reconnected and replayed.

Validation on 2026-09-26: `dotnet test Mainframe.slnx -c Release` passed
**186 tests** with no skips. Debug builds and provider/client/host tests also pass.
Coverage includes all creation
dispositions, metadata/allocation, retained replacement, delete cancellation,
cleanup versus close, sharing, lock quotas, directory markers and concurrent
changes, namespace/permission filtering, Unicode mount aliases, and permanent
invalidation after observed grant loss. Method-specific routing ignores unknown
optional JSON fields, so those fields cannot select a different volume queue. Migration failure injection preserves
schema-1 IDs, bytes, and timestamps. Separate Windows host processes are killed
around durable deletion intent, cleanup, replacement, and reclamation, then reopened.

The transport tests run 10,050 exchanges with bounded records and exercise
cancellation/retirement races, credit overruns, retained output, and legacy limits.
A real isolated TLS host also handles 10,050 root queries followed by reading and
verifying a 257 MiB sparse file on the same connection without reconnecting. Existing
Hello, execution, ConPTY, SDK authorization, storage, and unrestricted-password
tests remain in the suite. Tests use isolated state directories and containers,
never the operator's live volumes or registrations. Shared output is built under
`bin/Debug/net10.0` and `bin/Release/net10.0`. No WinFsp/Explorer acceptance or Linux
support is claimed by these tests.

Next is the separate WinFsp adapter: pin and qualify its runtime/.NET binding and
licenses; use Mainframe.Client without SQLCipher or CLI subprocesses; export a
selected subtree through drive-letter or directory mount points; implement Windows
path/security callbacks, NTSTATUS mapping, bounded dispatch, cleanup/close, and
unmount/disconnect handling. Validate Explorer, copy-in/out, editor replacement,
sharing and locks concurrent with SDK clients, flush failures, and process loss on
a real mount. Windows filesystem compatibility is not claimed before that testing.
Persistent ACLs, alternate streams, reparse points, hard links, compression,
Windows sparse-file controls, cross-volume rename, and recursive deletion remain
unsupported. Linux, peers, host-directory providers, and shell filesystem navigation
remain separate work.
