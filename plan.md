# Mainframe plan

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
- `mf help`, `mf status`, and `mf version`, including help/version aliases.
- Structured exit codes and separate stdout/stderr output.
- Local .NET tool packaging, MIT license metadata, and Git ignore rules.

The kernel, peer protocol, storage, jobs, and Windows filesystem adapter are not
implemented. All commands and contracts below are proposals, not available APIs.

## Layers and project boundaries

```text
CLI / interactive console / future Windows filesystem adapter
                           |
                    Mainframe client
                           |
             Kernel API and syscall dispatcher
                    /                  \
          Local module handler    Remote kernel connection
                    |                  |
             Storage / jobs / devices / services
```

Proposed projects:

| Project | Responsibility |
| --- | --- |
| Mainframe.Cli | Command parsing and presentation |
| Mainframe.Contracts | Versioned requests, responses, resource IDs, errors |
| Mainframe.Core | Dispatch, namespace, authorization, module contracts |
| Mainframe.Host | Persistent kernel process (`mfd`) and network endpoints |
| Mainframe.Client | Connections, authentication, routing, streaming |
| Future adapter project | Windows filesystem integration |

CLI commands are wrappers around typed syscall contracts. For example, `mf cat`
performs open/read/close operations. The Windows adapter calls the same API; it
must not launch CLI subprocesses and parse their output. Keep the core independent
of Windows filesystem integration.

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
Centralize namespace routing and permission enforcement in the core. Start with
trusted modules; consider separate worker processes for crash isolation later.

Storage providers declare features such as random access, writes, atomic rename,
snapshots, and change notifications. Do not pretend that a sensor stream supports
the same semantics as a persistent file.

## Peer connections and syscall protocol

Start with explicit peer addresses over a LAN or VPN and direct connections.
Use persistent authenticated connections; TCP with TLS is the initial transport
direction. Choose the RPC framework and serialization format before implementation.
Exchange identity, compatible protocol versions, and capability manifests during
the handshake. Define manifest updates and invalidation when modules change or
peers disconnect.

Joining requires an invitation or administrator approval and establishes a node
identity. Discovery alone does not authorize membership. Authenticate callers,
authorize each operation, and preserve caller identity across forwarding without
trusting an arbitrary identity field supplied by a client.

Requests carry a request ID, syscall name/version, target resource, authenticated
caller context, deadline, and typed arguments. Responses carry the request ID and
a typed result or structured error. Support bounded messages, streaming,
backpressure, cancellation, and limits on concurrent work.

Distinguish unsupported contracts, denied access, unavailable resources, expired
deadlines, and unknown operation outcomes. A lost response does not prove the
operation did not execute. Retry only operations with suitable idempotency or
deduplication rules. Bound forwarding to prevent routing loops.

## Routing and the single-machine experience

Normal operations name logical resources, not their physical host:

```text
mf ls /vol/projects
mf cat /vol/projects/readme.txt
mf jobs
mf services
```

The registry associates syscall providers with resource scopes and readiness:

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
retain owning node and session identity. Define close, timeout, and node-restart
behavior so stale handles cannot accidentally reference new resources.

Administrative commands expose physical topology: `mf nodes`, `mf node inspect`,
`mf volume inspect`, and `mf job inspect`. One logical machine should not hide
failures or make diagnosis difficult.

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
mf mount archive /vol/archive
mf export / --drive M:
mf export /vol/archive --directory C:\Mainframe\Archive
```

Start with one authoritative owner per volume and remote access from other nodes.
If the owner disconnects, report the volume as unavailable. A shared namespace
does not imply replicated storage. Later replication policies must specify which
copies acknowledge a write, when success means durable storage, and how recovery
and degraded operation work. Do not silently downgrade durability.

Define filesystem semantics deliberately: concurrent access, file sharing,
case sensitivity, timestamps, atomic replacement, flush, directory enumeration,
cache invalidation, and notifications. Editor saves involving temporary files and
rename must work, not only simple byte writes.

## Coordination and failures

Peer discovery and capability exchange do not establish resource ownership or
resolve conflicting writes. Keep coordination separate from syscall transport.

Initial proposal: one designated coordinator owns shared namespace metadata,
membership decisions, and scheduling. Any node may also act as a gateway, storage
provider, or worker. Losing the coordinator stops new authoritative changes and
new job assignments; specify which existing operations may continue safely.

Later, evaluate an established consensus implementation for three voting
coordinators. A majority can make authoritative changes; a minority must stop
them. Prevent stale owners/workers from committing operations after reassignment,
using ownership generations or equivalent fencing checked at the resource.
Metadata consensus does not itself replicate file contents or recover running jobs.

Do not promise that two machines can always make independent progress during a
network split while maintaining one consistent writable system.

## Jobs and services

Provide durable job IDs, a job spool, progress, logs, cancellation, and explicit
retry policies. Jobs should survive a CLI disconnect. Placement can consider
memory, GPU capability, installed tools, data locality, and attached devices.

```text
mf run index-documents --volume projects
mf run render-scene --requires gpu
mf job logs job-0042 --follow
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
- A future `mf console` provides an interactive station-style interface.
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

## Implementation milestones

1. **CLI foundation (implemented).** Keep commands modular; extend the argument
   contract as real operations are introduced.
2. **Local kernel.** Add contracts, host, client, and dispatcher. Implement identity,
   health, and capability discovery through the API. CLI status reports live state.
3. **Two linked kernels.** Establish authenticated peers, exchange manifests, and
   route a remote read-only syscall. Both CLIs show the same mainframe identity and
   both nodes. Verify incompatible versions, unauthorized peers, disconnection,
   deadlines, and reconnection without stale registry entries.
4. **Shared namespace and owner-based storage.** Add internal mounts and file
   operations; read/write a volume through either node. Verify permissions,
   concurrent access, owner loss, and stale handles.
5. **Distributed jobs.** Add durable submission, placement, logs, cancellation,
   and explicit failure/retry rules. Verify ambiguous outcomes and worker loss.
6. **Windows export.** Implement the selected adapter against the same client API.
   Verify normal editor workflows and that CLI and Windows see the same data.
7. **Resilience and operator tools.** Add replication, coordinator failover,
   snapshots, boot profiles, and recovery incrementally with failure tests.

## Decisions still open

- RPC framework, serialization, streaming protocol, and version evolution.
- Invitation flow, certificate issuance/revocation, and operator authentication.
- Persistent metadata format and coordinator implementation.
- File naming, consistency, caching, locking, and durability contracts.
- Execution isolation and supported job/module formats.
- Whether a distributed actor framework such as Orleans is useful for services;
  it is a candidate, not a dependency decision or a substitute for storage semantics.

## References

- [WinFsp tutorial and mount points](https://winfsp.dev/doc/WinFsp-Tutorial/)
- [WinFsp license and FLOSS exception](https://github.com/winfsp/winfsp/blob/master/License.txt)
- [Microsoft ProjFS provider overview](https://learn.microsoft.com/en-us/windows/win32/projfs/provider-overview)
- [etcd failure behavior](https://etcd.io/docs/v3.7/op-guide/failures/)
- [Orleans overview](https://dotnet.github.io/orleans/docs/overview/)
