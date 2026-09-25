# Implemented unary protocol

The first kernel supports TLS 1.3 with operator client certificates, protocol major
1 and the required feature `unary-rpc`. Only the `terminal` role is accepted by the
initial loopback host. Streaming frame discriminators are reserved but do not
enable streaming operations.

[`unary-v1.schema.json`](unary-v1.schema.json) publishes payload definitions selected
by the frame type: `hello`, `welcome`, `request`, `response`, `error`, and `goAway`.
Schemas describe JSON values; the parser additionally rejects duplicate property
names and limits depth to 32. Unknown properties are ignored. String bounds in
.NET are measured in UTF-16 code units. The host requires a negotiated frame limit
of at least 1,024 bytes, up to the protocol maximum of 1,048,576 bytes.

[`hello-v1.hex`](hello-v1.hex) is a complete plaintext application frame before TLS:
a 20-byte big-endian header followed by 131 UTF-8 payload bytes. It has type `0x0001`,
zero flags and zero exchange/channel IDs. Its compact JSON is:

```json
{"versions":[1],"optionalFeatures":[],"requiredFeatures":["unary-rpc"],"role":"terminal","clientName":"mf","maxFrameBytes":1048576}
```

The golden-byte test compares generated output with this fixture. Wire control
serialization uses explicit source-generated contracts. Application arguments and
results can be supplied as `JsonElement` or `JsonNode`; arbitrary CLR object graphs
are not implicitly serialized.

The initial client serializes requests, uses increasing odd exchange IDs and a
10-second call deadline, and does not retry calls. Cancellation or expiry after a
call begins closes the connection, so a delayed response cannot be mistaken for a
later call. Remote errors preserve their stable code and outcome. PING/PONG remains
active while the connection is idle. A GOAWAY stops new requests and permits an
active call to drain for at most ten seconds.

See [packets.md](../../packets.md) for the complete protocol target and deferred
streaming, peer delegation, enrollment, and flow-control behavior.
