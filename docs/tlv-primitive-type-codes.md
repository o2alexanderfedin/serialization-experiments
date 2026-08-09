# Primitive type codes

Design for carrying typed values — integers, floats, booleans, null, binary — on the TLV
format described in [mapping open-vocabulary XML onto TLV](xml-to-tlv-dynamic-tag-table.md).

This is **phase A** of supporting arbitrary object serialization. It changes the wire format
and nothing else: no object mapper, no property naming, no schema. Those are separate designs
that depend on this one, and are listed at the end.

> **Implemented.** `BYTES`, `NULL`, `FALSE`, `TRUE`, `UINT`, `SINT`, `F32`, `F64` and `GUID`
> are on the wire; the rest of the registry is allocated but not emitted. Measured on 1,000
> records of mixed scalar data: **38,972 bytes typed against 53,388 stringified, 27% smaller,
> with 24% less allocation**. Every pre-existing document encodes to the same bytes it did
> before. The [performance report](tlv-performance.md) carries the numbers and the one case
> where typed values lose.

## The problem

Every value the format can carry today is UTF-8 text. Anything that is not text has to be
stringified, and the frame charges a `Length` byte for a size that is often already known:

| Value | As text today | Ideal |
|---|---:|---:|
| `true` | 6 B (`Type` + `Length` + `"true"`) | 1 B |
| `42` | 4 B | 2 B |
| `3.14159265358979` | 18 B | 9 B |
| `DateTime` as ISO-8601 | 30 B | 9 B |
| `Guid` | 38 B | 17 B |

Two costs are tangled here. The larger one is the text representation itself. The smaller,
but structural, one is the `Length` field: a `double` is always eight bytes, so spending a
byte to say so is waste on every single value.

Removing `Length` is not free. **Universal skippability is the property that makes unknown
frames survive** — a reader can step over a frame it does not understand because the length
is right there. It is what lets an unknown element name, and an unknown type tag, round-trip
unchanged, and it is the concrete advantage this format has over Avro's unions, where an
unrecognised branch cannot even be stepped over. Any scheme that drops `Length` has to keep
that property by other means.

## The type byte

Split the Type byte into two nibbles: the high nibble says **how to skip the frame**, the low
nibble says **what it is**.

```
   T y p e   b y t e
   ┌───────┬───────┐
   │ SSSS  │ TTTT  │
   └───┬───┴───┬───┘
       │       └── which type, 16 per shape
       └────────── shape: how a reader skips it, knowing nothing else
```

A decoder that has never heard of type `0x63` still knows it is shape `6`, so the payload is
eight bytes, so the next frame starts nine bytes along. Skippability survives without a
`Length` field.

### Shapes

| Shape | Payload | How to skip |
|---|---|---|
| `0x0_` | varint length, then that many bytes | read the varint, skip that many |
| `0x1_` | none | skip 0 |
| `0x2_` | one canonical varint | read the varint |
| `0x3_` | 1 byte | skip 1 |
| `0x4_` | 2 bytes | skip 2 |
| `0x5_` | 4 bytes | skip 4 |
| `0x6_` | 8 bytes | skip 8 |
| `0x7_` | 16 bytes | skip 16 |
| `0x8_` | 32 bytes | skip 32 |
| `0x9_` | 64 bytes | skip 64 |
| `0xA_` | 128 bytes | skip 128 |
| `0xB_`–`0xE_` | **reserved** | cannot be skipped; a decoder must reject |
| `0xF_` | varint subtype, varint length, then that many bytes | read both, skip that many |

The fixed shapes are powers of two from 1 to 128 because that is what real primitives are:
1 for a byte, 2 for `Half`, 4 for `float`, 8 for `double`, 16 for `Guid` and `decimal`, 32
for SHA-256, 64 for SHA-512. Nothing needs 3 or 5.

`0xB_`–`0xE_` are the deliberate hole. They can never be allocated later, because a reader
written today cannot skip them — allocating them would be the ossification this format is
trying to avoid. Every future type goes into an existing shape or through `0xF_`.

### Backward compatibility

Every type code the format uses today is length-prefixed, so all five already sit in shape
`0x0_`:

| Existing | Shape | Unchanged? |
|---|---|---|
| `0x01` `ELEMENT` | `0` — varint length | yes |
| `0x02` `TEXT` | `0` | yes |
| `0x03` `TEXT_REF` | `0` | yes |
| `0x04` `TEXT_ONCE` | `0` | yes |
| `0x05` `TYPED` | `0` | yes |

**No renumbering, and no existing document changes by a byte.** Today's format is a strict
subset of this one. That was luck rather than foresight, but it is worth stating plainly
because it means this phase carries no migration.

`0x00` stays reserved, so a zero byte is never a valid Type.

## Type registry

Codes marked **impl** are implemented in this phase. The rest are *allocated* — their
meaning is fixed so that a later phase cannot contradict it — but no encoder emits them yet,
and a decoder treats them like any other unknown type (see below).

### Shape `0x0_` — length-prefixed

| Code | Name | Payload | |
|---|---|---|---|
| `0x00` | — | reserved, never valid | |
| `0x01` | `ELEMENT` | name reference, then child frames | existing |
| `0x02` | `TEXT` | UTF-8; claims a value id | existing |
| `0x03` | `TEXT_REF` | varint value id | existing |
| `0x04` | `TEXT_ONCE` | UTF-8; claims no id | existing |
| `0x05` | `TYPED` | type reference, then one child frame | existing |
| `0x06` | `BYTES` | raw octets | **impl** |
| `0x07` | `BIGINT` | two's-complement, big-endian, minimal length | allocated |
| `0x08`–`0x0F` | — | reserved | |

### Shape `0x1_` — no payload

| Code | Name | | |
|---|---|---|---|
| `0x10` | `NULL` | the absence of a value | **impl** |
| `0x11` | `FALSE` | | **impl** |
| `0x12` | `TRUE` | | **impl** |
| `0x13`–`0x1F` | — | reserved | |

### Shape `0x2_` — one varint

| Code | Name | Encoding | |
|---|---|---|---|
| `0x20` | `UINT` | canonical LEB128 | **impl** |
| `0x21` | `SINT` | ZigZag, then canonical LEB128 | **impl** |
| `0x22`–`0x2F` | — | reserved | |

`UINT` and `SINT` carry any integer width. A separate code per width would let the wire
record whether a value was declared `int` or `long`, but nothing needs that: a mapper knows
the target property's type, and a schema-less reader only needs the number. Fixed-width codes
are allocated below for the cases where a caller genuinely wants a fixed cost regardless of
magnitude.

ZigZag matters. Protobuf's `int64` spends all ten bytes on any negative number because the
sign bit is set; `sint64` maps `-1 → 1`, `-2 → 3`, so small negatives cost one byte. Signed
values go through `SINT` for that reason.

### Shapes `0x3_`–`0xA_` — fixed width

| Code | Name | | Code | Name | |
|---|---|---|---|---|---|
| `0x30` | `I8` | allocated | `0x60` | `I64` | allocated |
| `0x31` | `U8` | allocated | `0x61` | `U64` | allocated |
| `0x40` | `I16` | allocated | `0x62` | `F64` | **impl** |
| `0x41` | `U16` | allocated | `0x63` | `TIMESTAMP` | allocated |
| `0x42` | `F16` | allocated | `0x70` | `GUID` | **impl** |
| `0x50` | `I32` | allocated | `0x71` | `DECIMAL` | allocated |
| `0x51` | `U32` | allocated | `0x80` | `SHA256` | allocated |
| `0x52` | `F32` | **impl** | `0x90` | `SHA512` | allocated |

Fixed-width integers are little-endian two's complement. Floats are IEEE 754 binary16 /
binary32 / binary64, little-endian.

`TIMESTAMP` is **100-nanosecond ticks since the Unix epoch**, signed 64-bit, always UTC.
Nanoseconds would have been the more interoperable unit but only spans ±292 years, which
does not reach either end of `DateTime`'s range; 100-ns ticks span ±29,227 years and are
exactly `DateTime`'s own resolution, so the mapping is lossless in both directions. Time
zones and offsets are the mapper's problem, not the wire's — this is an instant.

`DECIMAL` is the 16 bytes of a .NET `decimal` in its documented layout. Unallocated codes
within these shapes are reserved.

### Shape `0xF_` — extension

| Code | Name | |
|---|---|---|
| `0xF0` | `EXT` | subtype numbers allocated by this document |
| `0xF1` | `EXT_PRIVATE` | subtype numbers never allocated by this document |
| `0xF2`–`0xFF` | — | reserved |

Layout: `0xF0`, varint subtype, varint length, then that many bytes.

The split mirrors CBOR's IANA-registered versus private-use tag ranges and MessagePack's
reserved versus application ext types. It exists so that an application can define its own
domain types without any risk of colliding with a future version of this format — a
guarantee CBOR cannot make within its one-byte tag space, which RFC 8949 §7.1 notes is
already half allocated and "needs some curation to last for a few more decades".

Three bytes of header rather than one is the right trade here: extensions are the long tail
by definition, and paying for them uniformly would tax every `true` and every small integer.

## Unknown types are preserved, not rejected

Today the decoder throws on an unrecognised Type. **That changes.** A frame whose shape is
known but whose type is not decodes to a node carrying the raw type byte and payload, and
re-encodes to exactly the bytes it came from.

This is the same decision already taken for unknown type *names* in `TYPED` frames, for the
same reason: rejecting what you do not recognise ossifies a format, and RFC 8949 §5.4 says
so outright — treating an unknown tag as an error "can cause ossification and is thus not
encouraged". A reader built today should relay a document containing a type allocated
tomorrow without corrupting it.

Three cases remain errors, because none of them can be skipped or preserved honestly:

- `0x00` — reserved, and a zero byte is far more likely to be corruption or padding.
- Shapes `0xB_`–`0xE_` — the length is unknowable.
- A frame whose payload runs past its parent's end — already an error today.

## Canonical encoding

The format's existing rule is that a document has exactly one valid byte representation.
Varints must already be shortest-form. Extending that:

- **Fixed-width payloads** are exactly their shape's width; there is no short form.
- **`SINT` and `UINT`** must use the shortest varint, as now.
- **`BIGINT`** must use the minimal two's-complement length: no leading `0x00` on a positive
  value, no leading `0xFF` on a negative one.
- **NaN** must be the canonical quiet NaN for its width — `0x7E00` for binary16,
  `0x7FC00000` for binary32, `0x7FF8000000000000` for binary64. Other NaN payloads are
  rejected. Without this, every NaN bit pattern — and there are more than 2⁵² of them at
  binary64 — is a separate encoding of one value.
- **Negative zero is preserved.** `-0.0` and `0.0` are different values, not different
  encodings of one value, and `1/-0.0` differs from `1/0.0`.
- **Choosing between types is the producer's business, not a canonicity question.** `42` as
  `SINT` and `42` as `I32` are different frames; the encoder picks one from the declared
  type and the decoder reports which it saw. Canonicity constrains the bytes for a given
  type, not the choice of type.

## What this does not do

Deliberately out of scope, each because it belongs to a later phase or has no measured need:

- **No interning of non-text values.** A reference costs three bytes; a small integer costs
  two, so referencing one can only lose. It could pay for a repeated `Guid` or hash, but
  entangling that with the existing value table before there is a measurement to justify it
  would complicate both. Text interning is untouched.

  > **The measurement now exists, and it is worse than expected.** Designing phase B produced
  > it: a `Guid` that repeats costs **6 bytes** as interned text and **20 bytes** as a `GUID`
  > frame — **−233%**. Any document carrying a repeated identifier is made substantially larger
  > by this decision, which covers most real record data. Reopened as an open question in
  > [the object mapper note](tlv-object-mapper.md).
- **No arrays or objects.** `ELEMENT` already nests. Whether collections deserve their own
  frame is a question for the mapper phase, which will have the data to answer it.
- **No property names or ordinals.** Phase C.
- **No reference identity or cycles.** Phase D, and it stays reserved as documented.
- **No fixed-width integer encoders.** The codes are allocated; `SINT`/`UINT` cover the need
  until something measures otherwise.

## Interaction with a schema mode

When the reader already knows what type to expect — polymorphism off, a contract in hand —
the type byte is redundant. It is worth being explicit about what may be dropped later, so
that this phase does not accidentally rule it out.

**Do not simply omit the type byte.** Omitting it also omits the shape, and a reader that
cannot determine a frame's width cannot step over a field it does not recognise. That is
precisely Avro's bargain: its unions carry a branch index with no length, so an unknown
branch is unreadable rather than merely unknown.

Protobuf shows the way out. Its tag is `(field_number << 3) | wire_type`, so three bits of
shape travel inside the field tag: unknown fields stay skippable while the concrete type
comes from the schema. The shape nibble defined here folds into a field tag the same way —
`(ordinal << 4) | shape` — costing one byte for ordinals 0–7 and carrying the skip rule with
it. Phase C is where that gets designed and measured.

Two limits on the saving, worth recording now:

- It is one byte per value **that has a payload**, not one per field. `NULL`, `TRUE` and
  `FALSE` are shape `0x1_`: the type byte *is* the value. Removing it means adding a payload
  byte back, for no gain.
- It applies only where a schema is genuinely in force. A document that mixes schema-known
  and open regions has to say where the boundary is, and that boundary is itself framing.

Nothing in this phase depends on which way that goes. The type byte defined here is the
self-describing encoding; a schema mode is an alternative spelling of the same type space,
not a replacement for it.

**This is an invariant, not a preference: the shape is always on the wire.** No mode, option,
or schema may produce a frame whose width cannot be determined from the bytes alone. A byte
saved by making a document unskippable is not a byte saved — it converts an unknown field
from something a reader steps over into something that desynchronises everything after it,
and the failure is silent, because the reader has no way to know it has lost its place.

## Safety invariants

Every rule below is a decoder obligation. They are collected here because they are what makes
adding type codes safe, and because a later phase must not quietly weaken one for density.

1. **A frame's width is always derivable from its own bytes.** Shape nibble, or a length
   prefix, or both. No exceptions, in any mode.
2. **A payload must fit inside both its parent frame and the buffer.** A shape `0x8_` frame
   with 12 bytes remaining is an error, not a 32-byte read. Fixed shapes need this check as
   much as length-prefixed ones do — more, because there is no length field to sanity-check
   against.
3. **No allocation is sized by a declared length before that length is validated.** Already
   the rule for `TEXT`; it now also covers `BYTES`, `BIGINT` and extension payloads, which
   are the new places a hostile document could ask for a large buffer cheaply.
4. **Unknown does not mean trusted.** An unrecognised type is preserved as opaque bytes, not
   interpreted. It is never resolved, constructed, or executed — the same line already drawn
   for unknown type names in `TYPED` frames.
5. **One document, one encoding.** Every canonicalisation rule above exists to keep this
   true. It is what makes re-encoding byte-exact, and it is what stops two documents hashing
   or signing as one.
6. **Reject rather than guess.** `0x00`, shapes `0xB_`–`0xE_`, non-canonical varints,
   non-canonical NaN, and truncated payloads are all errors. None of them has a safe
   interpretation, and inventing one would only move the failure somewhere less obvious.

`TlvDecoderOptions` gains `AllowUnknownTypes`, defaulting to `true`, matching the existing
`AllowTypeNames` knob. Preservation is the default because an unknown frame is inert — it is
carried, never interpreted, and `UnknownNode` is a distinct record, so a consumer that fails
to handle it fails loudly rather than mistaking it for a value. Setting it to `false` refuses
such documents outright, for a peer that should only ever receive types it knows.

## Node model

Three additions, mirroring the shape split rather than the type list:

```csharp
public sealed record PrimitiveNode(byte Type, ReadOnlyMemory<byte> Payload) : Node;
public sealed record UnknownNode(byte Type, ReadOnlyMemory<byte> Payload) : Node;
```

`PrimitiveNode` holds a type the codec understands; `UnknownNode` holds one it does not, and
exists only so an unrecognised frame can be re-encoded unchanged. Keeping them as separate
records rather than one type with a flag means the "I did not understand this" case cannot be
silently confused with a real value in a `switch`.

Typed accessors — `PrimitiveNode.AsInt64()`, `.AsDouble()`, `.AsGuid()`, and matching
factories — sit on top. The raw payload stays available because the encoder needs it and
because re-encoding must be byte-exact.

An alternative was a record per primitive kind (`Int64Node`, `DoubleNode`, …). It reads
better at the call site but multiplies the `switch` in three encoder passes and two decoder
methods by the number of types, for no behavioural gain — every one of them is "write the
type byte, write the payload".

## Verification

The format's standing rule is that claims get measured, not asserted.

- **Golden vectors** for every implemented code: a hand-checked byte sequence per type,
  cross-checked against the worked example in the wire-format document.
- **Round-trip and canonical re-encode** for each type, including `-0.0`, `NaN`, `long.MinValue`,
  `ulong.MaxValue`, empty `BYTES`, and `Guid.Empty`.
- **Unknown-type preservation**: a hand-rolled document using an allocated-but-unimplemented
  code, and one using a code allocated to nothing at all, must both decode and re-encode to
  identical bytes for every defined shape.
- **Rejection**, one test per safety invariant: `0x00`; each of shapes `0xB_`–`0xE_`;
  non-canonical NaN at each float width; a fixed-width payload cut short by the buffer's end;
  the same cut short by its parent frame's end while bytes remain in the buffer, which is the
  case a naive bounds check passes; a `BYTES` or extension frame declaring a length larger
  than the document; and `AllowUnknownTypes = false` refusing a frame it would otherwise
  have preserved.
- **Randomised round-trips** extended to generate primitives, reusing the existing fixed-seed
  harness, including the single-byte-corruption check that must yield `TlvFormatException`
  and never an overflow or an index-out-of-range.
- **Mutation testing** on the shape decoder: an off-by-one skip width, a missing canonical
  check, and unknown-type rejection instead of preservation must each fail tests.
- **Measurement** via the existing exact `sizes` and `alloc` reports, plus a benchmark shape
  built from typed values so the payload claim in the table at the top is a number rather
  than an argument. Timing needs a quiet machine; allocation and size do not.

## Phases after this one

| | | Depends on |
|---|---|---|
| **B** | Object mapper, self-describing: POCO ↔ `Node` by property name, collections, null. Four length-prefixed frames — `FIELD`, `ARRAY`, `MAP`, `OBJECT`. Designed in [the object mapper note](tlv-object-mapper.md). | A |
| **C** | Ordinals opt-in: a contract assigning numbers, and a wire mode where a field's identifier is an ordinal rather than an interned name. Folds the shape nibble into the field tag, protobuf-style, so the concrete type comes from the schema while unknown fields stay skippable. | B |
| **D** | Identity and cycles: reference preservation, two-phase construction, an amplification budget. Optional. | B |

A is first because a mapper built on stringified numbers would bake the bloat in permanently,
and every measurement after it would be against a bad baseline.

### Correction: B's `FIELD` frame carries a `Length` after all

This table originally promised B "a `FIELD` frame, since a property has exactly one value and
so needs no `Length`". **That frame cannot be built.** A layout of "one varint, then one nested
frame" needs a shape meaning exactly that, and the shape nibble is full: `0x0_`–`0xA_` are
allocated, `0xF_` is the extension, and `0xB_`–`0xE_` can never be allocated because they carry
no width. Nothing is left to define, so `FIELD` sits in shape `0x0_` and pays the `Length` byte.

The saving is still reachable by inverting the frame — a name *marker* that precedes its value
as a sibling rather than a container that holds it, both self-delimiting — and `0x22` is
reserved for that. It is deferred because it would be the first frame in this format whose
meaning depends on the next frame, which is a property worth more than one byte per field until
a measurement says otherwise.
