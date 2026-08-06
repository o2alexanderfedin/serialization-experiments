# Emitting TLV length prefixes without buffering the value

## The problem

TLV frames a field as **Type, Length, Value** — in that order. The length must be on the
wire before the value it describes.

That is trivial for a scalar. It is not trivial for a tree: a node's serialized size is
unknown until its entire subtree has been walked, and the subtree may itself contain nested
TLV frames whose own headers contribute bytes to the parent's length.

The obvious workaround is to serialize each node into a buffer, measure the buffer, write
the header, then copy the buffer out. That costs memory proportional to the payload, and
for nested structures it tends toward a buffer per level.

## The approach: one traversal, two sinks

Write the traversal **once**, against an abstract sink, and run it twice with two different
implementations:

1. **Counting sink** — discards the bytes, only increments a counter.
2. **Real sink** — writes through to the stream.

```
size = Measure(root)          // pass 1: no allocation, just arithmetic
WriteHeader(type, size)
Emit(root, sink)              // pass 2: same walk, real bytes
```

Peak memory becomes **O(depth)** — the recursion stack — instead of O(payload). Nothing
holds serialized bytes at any point.

This is what Protobuf does: `CalculateSize()` followed by `WriteTo()`.

## The trap: naive two-pass is O(n·depth)

If the emit pass recomputes a nested length at the moment it writes that child's header,
every node gets re-measured once per ancestor. On a balanced tree that is O(n·log n); on a
degenerate chain it is O(n²).

## The fix, and the knob it gives you

Memoize the sizes computed in pass 1. Assign each node an index during the walk and fill a
flat `int[]` with its content size; pass 2 then reads sizes by index instead of recursing.
This is exactly what Protobuf's generated `_cachedSize` field is for.

Caching is a dial, not a binary:

| Cache | Extra memory | Time |
|---|---|---|
| Everything | O(n) ints (~4 B/node) | O(n) |
| Nothing | O(1) | O(n·depth) |
| Top *k* levels | O(nodes above *k*) | between |

Even "cache everything" is roughly 4 bytes per node against perhaps 20–100 bytes of
serialized payload per node — an order of magnitude better than buffering, in one
contiguous array rather than per-node allocations.

## Correctness constraints

**The two passes must agree byte-for-byte, or the frame is corrupt.** Every encoding
decision has to be deterministic across passes:

- variable-width integer encoding (varint widths)
- floating-point and date/time formatting
- culture invariance in any number or string formatting
- dictionary and set iteration order
- string encoding and any Unicode normalization
- whether an optional field is considered present

Two rules follow:

- **Count header bytes, not just payload.** A parent's length includes each child's tag and
  length bytes. With variable-width lengths, a header's own width depends on the content
  size it encodes.
- **Assert that bytes written equals the measured size.** Without this, drift between the
  passes silently corrupts the output. With it, the failure is loud and points at the
  offending node.

## When two-pass is not available

Two-pass requires a **replayable source**. A lazy `IEnumerable`, a database cursor, or data
arriving off a socket cannot be walked twice. The alternatives, each with its cost:

| Technique | How | Cost |
|---|---|---|
| Seek and backpatch | Reserve a fixed-width length, write the value, seek back, patch it | Needs a seekable sink — file, not socket; forfeits variable-width compaction |
| Indefinite length + EOC | BER's own answer: omit the length, terminate with an end-of-contents marker | Illegal in DER, so unusable where canonical encoding matters (anything signed); destroys skip-ahead |
| Chunked framing | Emit bounded chunks, each with its own length (HTTP chunked, CBOR indefinite-length strings) | Changes the wire format |
| Reverse encoding | Build back-to-front: emit children at the tail, prepend the header once the length is known (FlatBuffers) | Single pass and elegant, but holds the whole message in memory |

## Recommendation

Two-pass with a counting sink and a flat size cache. It preserves canonical fixed-length
TLV, works on non-seekable sinks, keeps memory at O(depth) plus ~4 bytes per node, and
costs roughly 2× traversal CPU — usually cheap, since pass 1 touches no I/O and is
branch-predictable arithmetic.

The single design decision that makes it hold together: **express the encoder against a
sink interface so the same traversal code serves both passes.** If measuring and emitting
are two separately maintained code paths, they will drift — and the drift shows up as
corrupted wire data, not a compile error.
