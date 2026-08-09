# Object mapper

Design for carrying arbitrary .NET objects on the TLV format described in
[mapping open-vocabulary XML onto TLV](xml-to-tlv-dynamic-tag-table.md), using the typed
values from [primitive type codes](tlv-primitive-type-codes.md).

This is **phase B** of supporting arbitrary object serialization. It adds four frame types and
a mapper; it does not add ordinals, schemas, or reference identity. Those are phases C and D.

> **Design only.** Nothing here is implemented yet. The size numbers, however, *are* measured —
> `FIELD` has `ELEMENT`'s exact layout, so the existing encoder can measure this phase's
> framing before it is built. Doing so overturned the estimate this document was first written
> around: typed values are **not** uniformly smaller for object data, and on one realistic
> record shape they are larger. See [what this replaces](#what-this-replaces-measured).

## The problem

Phase A made values cheap. It did not make *objects* expressible. Today the only structure the
format has is `ELEMENT`, which carries a mandatory name and an ordered list of children — an
XML element. An object is not an XML element:

- Its properties have names, but a property holds **exactly one** value, where an element holds
  any number.
- A collection has **no** name, and `ELEMENT` has nowhere to put "no name": name id `0` is
  already spoken for as the literal-follows discriminator.
- A dictionary's keys are **data**, not vocabulary.

Encoding all three as `ELEMENT` would work, in the sense that the bytes would round-trip. It
would also make an object and a document indistinguishable on the wire, which is the part that
matters, and which the next section is about.

## Why distinctness is worth a type code

`ELEMENT("Age", [SINT 42])` and a property `Age = 42` would be the same bytes. A decoder
handed those bytes could not say which the producer meant, so `Node → object` would have to
guess from the target type, and two producers that meant different things would be
indistinguishable to any consumer that did not already know.

The cost of fixing that is one type code per distinct concept. The benefit is that "this is a
property", "this is a collection", "this is a map" and "this is an object" become facts in the
bytes rather than conventions in the caller's head. That is the same trade `TEXT_ONCE` made
against a discriminator field, and the same one `TYPED` made against reusing `ELEMENT`.

## Wire format

Four new codes, all in shape `0x0_`, all length-prefixed. **Every reader built today already
skips them correctly**, because the shape nibble is what governs skipping and shape `0x0_` has
not changed. No existing document changes by a byte.

| Code | Name | Payload |
|---|---|---|
| `0x08` | `FIELD` | a name reference, an optional literal, then exactly one child frame |
| `0x09` | `ARRAY` | zero or more child frames, no name |
| `0x0A` | `MAP` | an even number of child frames, alternating key and value |
| `0x0B` | `OBJECT` | zero or more `FIELD` frames, no name |

### `FIELD` value

| Order | Field | Size | Present when | Notes |
|---|---|---|---|---|
| 1 | `NameRef` | 1–5 B | always | `0` = literal follows; `n>0` = name id `n-1` |
| 2 | `NameLen` | 1–5 B | `NameRef == 0` | Byte count of `Name` |
| 3 | `Name` | `NameLen` B | `NameRef == 0` | UTF-8, no terminator. Assigns the next id |
| 4 | `Child` | rest | always | Exactly one complete frame, filling `Length` exactly |

Identical in layout to `TYPED`, and for the same reason: a property tags a single value. A
frame that does not end where its one child ends is malformed — zero children and two children
are both errors, not tolerated shapes.

Field names share the **element name table**. A field named `Age` and an element named `Age`
take one id between them, which is what a document mixing objects and markup wants. No
existing document shifts an id, because none contains a `FIELD`.

### `ARRAY` value

| Order | Field | Size | Notes |
|---|---|---|---|
| 1 | `Items` | `Length` B | Zero or more complete frames, filling `Length` exactly |

Item count is not stored — read frames until the parent's `Length` is consumed, exactly as
`ELEMENT` does with its children. Items need not share a type.

### `MAP` value

| Order | Field | Size | Notes |
|---|---|---|---|
| 1 | `Entries` | `Length` B | An **even** number of complete frames: key, value, key, value |

An odd count is malformed. Keys are ordinary value frames, so a key may be text, an integer, a
`GUID`, or anything else the format can carry.

### `OBJECT` value

| Order | Field | Size | Notes |
|---|---|---|---|
| 1 | `Fields` | `Length` B | Zero or more `FIELD` frames, filling `Length` exactly |

A child that is not a `FIELD` frame is malformed. Field order is preserved as written; the
format does not sort, because sorting would make re-encoding non-exact for any producer that
cared about order.

### Why `MAP` and not string-keyed `OBJECT`

A `Dictionary<string, T>` is object-shaped, so mapping it onto `OBJECT` is tempting and costs
no new code. It is the wrong call, and the reason is the id space.

Field names intern into the **name** table, which has no occurrence heuristic. The `ClaimsId`
rule — a value must be at least two bytes *and* occur more than once before it claims an id —
guards the **value** table only. Element names are a small repeated vocabulary, so that
asymmetry has never mattered.

Dictionary keys are not vocabulary. A dictionary keyed by user id would claim a name id per
key, and the id space is dense and shared: once it passes 127, every later `NameRef` in the
document costs two varint bytes instead of one, including every ordinary element name. A
hostile or merely large document could make every name in the file more expensive.

Routing keys through ordinary value frames avoids all of it. Repeated keys intern through the
existing value heuristic, unique keys are emitted as `TEXT_ONCE` and claim nothing, and
non-string keys work without a special case.

### Type code budget

Shape `0x0_` had eight free codes (`0x08`–`0x0F`); this phase spends four. `0x0C`–`0x0F`
remain, the other shapes are largely empty, and `0xF_` exists for the long tail. Worth
recording rather than discovering later: **one phase consumed half of one shape's remaining
space**, and a phase C or D that wants a new structural frame has four codes left to work with.

## What this replaces, measured

`FIELD` has exactly `ELEMENT`'s layout — type, length, name reference, one child — so the
existing encoder can measure this phase's framing before a line of it is written. The numbers
below are the **marginal** cost of one more field in a 400-field document, which strips out
first-occurrence literals and gives the steady state with names already interned.

**These are measured, not estimated.** Size is exact and load-independent, so a loaded machine
does not affect them.

| Value profile | as text | as a typed frame | saving |
|---|---:|---:|---:|
| `bool`, repeated | 6 B | 4 B | 33.3% |
| `bool`, alternating | 6 B | 4 B | 33.3% |
| small `int`, one repeated value | 6 B | 5 B | 16.7% |
| small `int`, 0–99 | 6 B | 5 B | 16.7% |
| `int`, all distinct, ~10⁶ | 12 B | 7 B | 41.7% |
| `double`, all distinct | 22 B | 12 B | 45.5% |
| `Guid`, all distinct | 41 B | 20 B | 51.2% |
| **`Guid`, one repeated value** | **6 B** | **20 B** | **−233%** |

And a whole record — `Name` (8 chars, 100 distinct), `Age` (0–99), `Score` (a `double`),
`Active` (a `bool`):

| | text | typed |
|---|---:|---:|
| per record | 29 B | **30 B** |

**Typed encoding loses on that record.** Not by much, but the direction is the point, and it
would have been reported as a ~29% win if this table had stayed arithmetic. My estimate said
24 B → 17 B; the measurement says 29 B → 30 B.

### Why the estimate was wrong

Two effects, both of which the arithmetic ignored:

1. **Value interning already captures repeated scalars.** `"42"` and `"true"` do not cost
   `TEXT_ONCE`'s four and six bytes in a real document — they repeat, so they intern, and every
   occurrence after the first costs a three-byte `TEXT_REF`. Typed values are competing against
   3 B, not against 6 B. The perf report's [existing note](tlv-performance.md) that typed
   values "lose on round numbers" is the same effect seen from a different angle.
2. **A `double` is nine bytes framed whatever it holds.** `i * 0.5` spells as `"100.5"` — five
   characters, seven bytes framed. Short decimal spellings beat binary64 outright.

So the size outcome is **a property of the data, not of the format**. Typed values win big on
high-cardinality values with long spellings — distinct GUIDs, large integers, irrational
doubles — and lose on low-cardinality values of any type, because interning already solved
those.

### The repeated-`Guid` result is a finding about Phase A

Phase A decided not to intern primitives, on the arithmetic that a reference costs three bytes
and a small integer costs two, and recorded: *"Revisit only for repeated GUIDs or hashes, and
only with a measurement."*

**This is that measurement, and it is −233%.** A `Guid` that repeats costs 6 B as interned text
and 20 B as a `GUID` frame. Any document with a repeated identifier — a foreign key, a tenant
id, a correlation id, which is to say most real record data — is made substantially *larger* by
Phase A's headline feature. That is a Phase A decision to reopen, not something for this phase
to work around, and it is listed under open questions below.

### Framing saves nothing

The three bytes of `FIELD` header are identical to `ELEMENT`'s. **This phase changes no byte of
framing cost**; every byte in the table above comes from Phase A's typed values, and Phase B
merely makes them reachable from an object. The framing saving is what the sibling marker
below would buy, and it is deferred.

### Deferred: the sibling marker

Phase A's plan sketched "a `FIELD` frame, since a property has exactly one value and so needs
no `Length`". That frame cannot exist. A frame laid out as "a varint, then one nested frame"
needs a **shape** meaning exactly that, and the shape nibble is full: `0x0_` through `0xA_` are
allocated, `0xF_` is the extension, and `0xB_`–`0xE_` can never be allocated because they carry
no width. There is nothing left to define.

The goal is still reachable, by inverting the frame. Rather than a container that *holds* a
value, make the name a **sibling frame that precedes** one — `0x22` in shape `0x2_` for a name
reference, `0x08`'s literal form for a first occurrence. Both are self-delimiting, so skipping
is unaffected, and it saves the `Length` byte: two bytes of naming overhead per repeated field
instead of three.

It is deferred because it would be the first frame in this format whose meaning depends on the
*next* frame. Everything else is a complete thing on its own, and giving that up for one byte
per field should be a decision backed by a measurement rather than an estimate. `0x22` stays
free.

## The mapper

Two ways to discover a type's properties, and two ways to reach the wire. They are orthogonal:
**two producers of one contract, and two consumers of it** — not four mappers.

```
reflection scan  ─┐                      ┌─→  Node tree ─→ the existing encoder
                  ├─→   TypeContract  ───┤
generated code   ─┘                      └─→  direct two-pass against the sink
```

```csharp
public sealed record TypeContract(
    string WireName,
    IReadOnlyList<PropertyContract> Properties,
    ConstructorBinding? Construction);

public sealed record PropertyContract(
    string Name,
    Type DeclaredType,
    Func<object, object?> Get,
    Action<object, object?>? Set);
```

### Why one contract rather than two mappers

Two hand-written paths that must agree byte-for-byte will drift, and drift surfaces as
corrupted bytes rather than a compile error. That is not a hypothesis — it is the finding
already recorded for the two-pass encoder in
[length prefixes without buffering](tlv-length-prefix-without-buffering.md), whose whole
design rule is that a single traversal expressed against a sink interface serves both passes.

The same move applies here. Reflection and source generation are not two mappers; they are two
ways to produce one contract, and one mapper consumes it. The generator emits **no
serialization logic at all** — its entire job is to emit the contract that a reflection scan of
the same type would have produced. That makes it a far smaller generator than MemoryPack's, and
it makes byte-for-byte agreement structural rather than something a test has to keep catching.

The test then writes itself: for every type in the corpus, the generated contract must equal
the reflected one.

### What source generation actually buys

Not what it is usually sold as, in this design.

`Func<object, object?>` boxes every value-type property, on **both** paths. Keeping the
signature identical is precisely what makes the seam hold, so the generated path allocates the
same as the reflected one. What it does buy:

- **Native AOT and trimming.** `Expression.Compile()` and `System.Reflection.Emit` are
  unavailable under Native AOT; expressions fall back to an interpreted form that is *slower
  than plain reflection*. Generated code has no such cliff, and the trimmer can see it.
- **No startup scan**, and no `[DynamicallyAccessedMembers]` annotations to keep correct.

A generic specialization would remove the boxing, at the cost of the two paths no longer being
the same type. That is a later phase's trade, and it should be stated plainly rather than
letting the generator be sold as an allocation win it is not.

### Why both wire paths

The `Node` tree costs a `PrimitiveNode` and a payload array per property, and that will
dominate this phase's allocation. It costs **zero bytes on the wire** — direct encoding changes
allocation and time, never a single emitted byte — so it cannot distort the size measurements
this phase exists to produce.

It also sidesteps a correctness hazard. Two-pass encoding needs a *replayable* source, and a
lazy `IEnumerable` is exactly what cannot be walked twice. An arbitrary object can expose one.
The tree materializes it once; the direct path must materialize any enumerable that is not
already an `ICollection` into a pooled buffer during the counting pass, and read that buffer in
both passes.

Building both in one phase means the direct path is measured against a real baseline rather
than against an assertion.

### Allocation must be attributed

The `Node` tree is the mapper's cost, not the format's. The `alloc` report has to separate
mapper allocation from codec allocation, or Phase B will make the format look worse than it is
and the number will be quoted later out of context. This has happened once already in this
repo, with `text-heavy`.

## Type coverage

| | Encoding |
|---|---|
| Scalars | Phase A's typed codes |
| `string` | `TEXT` / `TEXT_ONCE` / `TEXT_REF`, unchanged |
| `null` | a `NULL` frame |
| Enums | the underlying integer, `SINT` or `UINT` by signedness |
| Arrays, `List<T>`, `IEnumerable<T>` | `ARRAY` |
| `IDictionary<K,V>` | `MAP` |
| POCOs and records | `OBJECT` of `FIELD` |
| A declared type holding a derived instance | `TYPED` wrapping the above |

**Enums** ride as their underlying integer rather than their name. The name is more portable
across versions — renaming a member breaks an integer encoding silently — but it multiplies the
byte cost of the single most common small value in real data. Integer, and the mapper knows the
target type, so it can convert back.

**Constructor binding.** Records and immutable classes have no setters, so `FIELD` names are
matched to constructor parameters: exact match first, then case-insensitive, since a record
generates parameter `name` from property `Name`. An ambiguous match is an error, not a guess.
Without this the mapper could not round-trip this repository's own types, which are records
throughout.

**Null is emitted, not omitted.** A null property costs four bytes as `FIELD` + `NULL`, and
omitting it would cost zero. Omitting it would also make `null` and *absent* the same document,
and "one document, one encoding" is a standing invariant of this format — it is what makes
re-encoding byte-exact and what stops two documents hashing as one. Omission stays available as
a measured option, behind a flag, if sparse objects turn out to justify it.

## Safety invariants

Phase A's six invariants continue to hold unchanged. This phase adds one, and it exists because
the mapper is the place the existing one could quietly die.

Phase A's rule is that **the codec never maps a type name to a `System.Type`**. The mapper is
not the codec. It is the layer whose job is to produce objects, and a mapper that resolves an
arbitrary embedded name is `BinaryFormatter`, which was removed from .NET 9 over exactly this.

> **7. The mapper resolves a wire type name only through a caller-populated registry.
> `Type.GetType(name)` appears nowhere, at any layer, ever.**

The set of constructible types is enumerated by the application, not derived from the document.
An attacker who controls the document therefore controls *which* of the registered types is
selected — a real consideration, and one the application can reason about — but never *what
types exist*, which is the property that turned deserialization into remote code execution in
every format that lost it. Avro shipped three versions of one allow-list before it held;
MessagePack-CSharp's failed because it did not recurse into generic arguments. A registry that
is a plain map from name to `Type`, with no resolution step, has nothing to recurse into.

The default wire name is the type's simple name, overridable per type. Collisions across
namespaces fail when the registry is built, which is where a name collision should fail.

An unknown type name is not an error by default: it decodes to a `TypedNode` carrying the name
as text, exactly as it does today, and the caller decides. `AllowTypeNames = false` continues to
refuse the frame outright.

## Node model

Four records, mirroring the four frames:

```csharp
public sealed record FieldNode(string Name, Node Value) : Node;
public sealed record ArrayNode(IReadOnlyList<Node> Items) : Node;
public sealed record MapNode(IReadOnlyList<KeyValuePair<Node, Node>> Entries) : Node;
public sealed record ObjectNode(IReadOnlyList<FieldNode> Fields) : Node;
```

Phase A rejected a record per primitive kind because every case in every switch would have said
the same thing — write the type byte, write the payload. These are the opposite: each one
traverses differently, so the switch growth is earned rather than mechanical. `ObjectNode` is
typed as a list of `FieldNode` rather than of `Node`, so the "children must be fields" rule is
enforced by the compiler on the encode side and needs checking only on decode.

Each addition touches three encoder passes — count, measure, emit — and two decoder methods.
The counting pass must recurse into all four, or a value inside an object would not be counted
for interning; that is the exact fault that `ValueInterningTests` was extended to catch when
`TypedNode` was added, and it will be extended again here.

## Verification

The format's standing rule is that claims get measured, not asserted.

- **Four-way byte identity.** {reflection, generated} × {node tree, direct} must produce
  byte-identical output for every type in the corpus. One parameterized test, and the strongest
  single property this design has.
- **Contract equality.** The generated contract equals the reflected contract, per type.
- **Golden vectors** for all four frames, hand-checked and cross-checked against this document
  programmatically rather than by eye — a golden vector copied by hand has already produced one
  test that failed for a reason resembling a real bug.
- **Round-trip** POCO → bytes → POCO for every covered type, including empty collections, empty
  objects, null fields, and a record bound through its constructor.
- **Malformed documents**, one test per rule: a `FIELD` with zero children; a `FIELD` with two;
  a `MAP` with an odd number of children; an `OBJECT` with a non-`FIELD` child; each of the four
  frames declaring a length that runs past its parent while bytes remain in the buffer.
- **Frame-level parsing**, never byte scanning. `0x08` is equally a `FIELD` type code and the
  length of an eight-character name; `Frames.Walk` exists because that ambiguity has already
  made two tests pass for the wrong reason.
- **Randomised round-trips** extended to generate objects, collections and maps, reusing the
  fixed-seed harness, including the single-byte-corruption check that must yield
  `TlvFormatException` and never an overflow or an index-out-of-range.
- **Security**: a document naming an unregistered type must not construct anything, and a
  document naming a registered type must construct only that type. Plus a source check for
  `Type.GetType(`, `Activator.` and `Assembly.Load` **excluding comment lines** — the exclusion
  is not pedantry, since the one existing occurrence of `Type.GetType` in this repository is
  inside a doc comment explaining that it is never called, and a naive grep fails on it.
- **Measurement** through the exact `sizes` and `alloc` reports, with mapper and codec
  allocation attributed separately, on a benchmark shape built from real objects. Timing needs a
  quiet machine; size and allocation do not.

## What this does not do

- **No ordinals and no schema.** Phase C.
- **No reference identity and no cycles.** Phase D. An object graph containing a cycle is an
  error in this phase, detected by the depth limit rather than by cycle detection — which is
  adequate but not good, and is one of the things D exists to fix.
- **No sibling-marker framing.** Deferred above, with `0x22` reserved for it.
- **No null omission.** Available later, behind a flag, backed by a measurement.
- **No generic specialization.** The boxing in `PropertyContract` is accepted so the contract
  seam stays single.
- **No primitive interning, and no per-value encoding choice.** Both are open questions above,
  and the first is Phase A's decision to revisit rather than this phase's to override.
- **No field ordering guarantees across recompiles.** Reflection returns properties in metadata
  order, which is not contractually stable. The contract fixes an order at build time and the
  document records what was written, so a document is self-consistent; two builds of the same
  type are not guaranteed to emit fields in the same order. Phase C's ordinals are the fix.

## Open questions this phase surfaced

Both come out of the measurement above, and neither is Phase B's to settle alone.

### ~~Should primitives be interned after all?~~ Settled — yes, and built

Repeated primitives now claim value ids through an `INTERN` wrapper (`0x0C`). The
`identifiers` shape went from 28,962 bytes to 15,121, **47.8% smaller**, and documents whose
primitives are all distinct are byte-for-byte unchanged.

The cost was measured rather than assumed, on the axis this project tracks: encode allocation
on `records`, whose primitives are all distinct, rose **59%**, because the counting pass was
hashing 3,000 values that could never intern. Filtering it to values whose frame is larger
than a reference cut that to **28%**. The residual is an occurrence table for 1,000 distinct
doubles — the same cost the format already pays for 1,000 distinct strings — so it is accepted
rather than capped, since a primitives-only cap would introduce an asymmetry text does not
have. Timing is still unmeasured; the machine was at load 153.

A `Guid` and the text spelling of that `Guid` remain different values, as they must for
re-encoding to stay byte-exact.

### Should the mapper choose text or typed per value?

Phase A already permits it: *"choosing between types is the producer's business, not a
canonicity question."* A mapper that spelled short doubles as text and long ones as binary64
would win both columns.

It is tempting and probably wrong. It makes the encoder's output depend on a heuristic rather
than on the declared type, so two versions of the mapper produce different bytes for the same
object, and "one document, one encoding" survives only in the weak sense that any *given*
encoder is deterministic. Recorded so the option is not rediscovered as new.

## Phases after this one

| | | Depends on |
|---|---|---|
| **C** | Ordinals opt-in: a contract assigning numbers, folding the shape nibble into the field tag protobuf-style. `FIELD` is the frame whose identifier becomes an ordinal, so this is a frame swap rather than a new mechanism. | B |
| **D** | Identity and cycles: reference preservation, two-phase construction, an amplification budget. Optional. | B |
