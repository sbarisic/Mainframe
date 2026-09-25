# Mainframe plan

Status: approved v1 design, with the initial Windows-local kernel milestone
implemented. The complete v1 runtime remains a target; see current scope below.

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

- .NET 10 CLI project with command registration and help routing.
- `mf help`, `mf version`, local `mf cluster init`, and live `status`, `health`, and
  `capabilities` queries, including structured JSON output and endpoint/state options.
- Structured exit codes and separate stdout/stderr output.
- Local .NET tool packaging, MIT license metadata, and Git ignore rules.
- `mfd serve`: loopback-only TCP/TLS 1.3 host with local operator mTLS.
- Private local CA/leaf certificates, stable identity, SQLite WAL/FULL metadata,
  schema checks, revocation checks, and a consistent metadata backup API.
- Strict bounded framing/JSON, unary RPC, deadlines, heartbeat, and explicit
  `kernel.describe`, `kernel.health`, and `kernel.capabilities` handlers.

This milestone runs as the current Windows account with private state. Dedicated
service-account installation, network invitations/certificate renewal, delegated
resource grants, peers, process execution/PTYs, storage, jobs, and Windows mounts
are not implemented. Initial certificates last seven days; expiry fails explicitly.
The host accepts local terminal clients only and rejects future stream/peer roles.
Full v1 commands and contracts below remain targets, not available APIs. Linux
verification is deferred to the user's second machine.
Support Windows/Linux and public endpoints, trusted users/programs, one persistent
coordinator, full terminal sessions, both storage providers, and registered local
programs. No automatic failover, executable deployment, hostile-code sandbox,
interactive session reattachment, or Orleans is included in v1.

## Layers and project boundaries

```text
mf terminal client -- TCP/TLS --> Host-side shell and process execution
                                       |
                              Ordinary program process
                                       |
                           Language SDK -- TCP/TLS
                                       |
Windows filesystem adapter --> Kernel API and syscall dispatcher
                                  /                  \
                        Local handler          Remote kernel
```

Proposed projects:

| Project | Responsibility |
| --- | --- |
| Mainframe.Cli | Local connection options, terminal I/O, one-shot execution |
| Mainframe.Contracts | Versioned requests, responses, resource IDs, errors |
| Mainframe.Core | Dispatch, namespace, authorization, module contracts |
| Mainframe.Host | Persistent kernel (`mfd`), host-side shell, process supervision, endpoints |
| Mainframe.Client | .NET session/RPC client, authentication, streaming |
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
mf connect server:7443
atlas> weather
atlas> bank
atlas> cat /vol/documents/report.txt

# One-shot execution from the host OS shell:
mf --endpoint server:7443 exec cat /vol/documents/report.txt
```

`mf connect` opens an interactive remote shell. The mainframe shell owns the
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
It specifies the v1 target and identifies the implemented unary subset separately.

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

Administrative commands expose physical topology: `mf nodes`, `mf node inspect`,
`mf volume inspect`, and `mf job inspect`. One logical machine should not hide
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

Start with one authoritative owner per volume and remote access from other nodes.
If the owner disconnects, report the volume as unavailable. A shared namespace
does not imply replicated storage. Later replication policies must specify which
copies acknowledge a write, when success means durable storage, and how recovery
and degraded operation work. Do not silently downgrade durability.

### Providers and names

Implement both providers in the initial storage milestone:

- Managed volume: one local SQLite database per volume with directories, metadata,
  and 64 KiB file-content chunks. Target documents and ordinary files first.
- Host-directory volume: an administrator-selected existing directory exposed
  through the same filesystem API.

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
owner. Byte-range locks are unsupported and return `NOT_SUPPORTED`. Do not cache
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
independent of presentation and storage implementation. Only the CLI foundation is
implemented along with the Windows-local unary kernel subset; later stages remain
pending and require their own implementation/testing.

1. **CLI foundation (implemented).** Keep commands modular; extend the argument
   contract as real operations are introduced.
2. **Protocol, identity, and coordinator (local subset implemented).** Framing,
   TLS 1.3, local bootstrap/operator authorization, SQLite identity metadata, and
   unary queries exist. Network enrollment/renewal, delegated grants, public
   hardening, and complete coordinator metadata remain pending.
3. **Program SDK and terminal execution.** Implement scoped bootstrap, pipe-mode
   execution, Windows ConPTY/Linux PTYs, cancellation, and terminal restoration.
4. **Two linked kernels.** Establish authenticated peers, exchange manifests, and
   route a remote read-only syscall. Both CLIs show the same mainframe identity and
   both nodes. Verify incompatible versions, unauthorized peers, disconnection,
   deadlines, and reconnection without stale registry entries. Register programs
   per host and relay a remote process's I/O through either entry kernel.
5. **Both storage providers and shared namespace.** Implement managed SQLite and
   host-directory volumes, internal mounts, handle routing, and durability contracts.
   Read/write through either node. Verify permissions,
   concurrent access, owner loss, and stale handles. Complete the two-machine
   weather/bank/cat fixture scenario, including `cat` on A reading storage on B.
6. **Distributed jobs.** Add durable submission, placement, logs, cancellation,
   and explicit failure/retry rules. Verify ambiguous outcomes and worker loss.
7. **Windows export (separate adapter milestone).** Implement the selected adapter against the same client API.
   Verify normal editor workflows and that CLI and Windows see the same data.
8. **Later work, outside v1.** Replication, coordinator failover, snapshots, program
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
| Storage | Both managed chunked SQLite and host-directory providers |
| Names | NFC, case-preserving/case-insensitive, Windows-friendly names |
| Execution | Trusted local executables/interpreters; no sandbox claim |
| Registration | Admin manifests; ambiguous names require version/node qualification |
| Terminal | ConPTY and Linux PTYs; immediate cleanup on detected session loss |
| Bootstrap | Single-use 30-second credential through inherited pipe/descriptor |
| Orleans | Not used in v1 |

## Validation and acceptance

- Protocol: fragmented/coalesced frames, malformed JSON, illegal transitions, ID
  collisions, slow readers, bounded memory, cancellation races, completion ordering.
- Identity: invitation expiry/replay, fingerprints, expired/revoked certificates,
  denied delegation, public-endpoint throttling, and bootstrap expiry/reuse.
- Coordinator: restart persistence, transactional enrollment, backup/restore,
  schema rejection, outage blocking, lease expiry, and recovery reconciliation.
- Storage: cross-node I/O, naming collisions, traversal/link rejection, sharing
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
