# Mapping open-vocabulary XML onto TLV with a dynamic tag table

Companion to [emitting TLV length prefixes without buffering](tlv-length-prefix-without-buffering.md).

## The problem

TLV assumes a small, fixed set of Types agreed in advance. XML tag names are an open
vocabulary — a sender can use an element nobody anticipated, and we still have to encode
it, transport it, and reconstruct its name exactly.

Scope here: XML-like structure, elements and text only, **no attributes**.

## The reframe

The apparent conflict comes from assuming the XML tag name must *become* the TLV Type. It
must not. **Type carries the category of node; identity moves into the Value.**

Elements-and-text XML has two node kinds, so the registry needs two entries — no matter how
many distinct tag names ever arrive.

A free consequence: **no closing tags.** The Length already delimits an element's extent,
so `</order>` costs zero bytes. XML spends roughly half its markup on close tags.

## Wire format

Little-endian varints (LEB128), 1-byte types.

### Frame — every node

| Offset | Field | Size | Encoding | Notes |
|---|---|---|---|---|
| 0 | `Type` | 1 B | uint8 | See registry |
| 1 | `Length` | 1–5 B | varint | Byte count of `Value` only — excludes Type and Length |
| 1+L | `Value` | `Length` B | per type | Layout depends on `Type` |

### Type registry

| Value | Name | Kind | Value contains |
|---|---|---|---|
| `0x01` | `ELEMENT` | constructed | NameRef, optional literal, then child frames |
| `0x02` | `TEXT` | primitive | UTF-8 bytes; assigns the next value id |
| `0x03` | `TEXT_REF` | primitive | varint id of a value defined by an earlier `TEXT` |
| `0x00`, `0x04`–`0xFF` | — | reserved | `0x00` deliberately unused so a zero byte is never a valid Type |

### `ELEMENT` value

| Order | Field | Size | Present when | Notes |
|---|---|---|---|---|
| 1 | `NameRef` | 1–5 B | always | `0` = literal follows; `n>0` = name id `n-1` |
| 2 | `NameLen` | 1–5 B | `NameRef == 0` | Byte count of `Name` |
| 3 | `Name` | `NameLen` B | `NameRef == 0` | UTF-8, no terminator. Assigns the next id |
| 4 | `Children` | rest | always | Zero or more complete frames, filling `Length` exactly |

Child count is not stored — read frames until the parent's `Length` is consumed.

### `TEXT` value

| Order | Field | Size | Notes |
|---|---|---|---|
| 1 | `Chars` | `Length` B | UTF-8. Empty text is legal (`Length == 0`). Assigns the next value id |

### `TEXT_REF` value

| Order | Field | Size | Notes |
|---|---|---|---|
| 1 | `ValueId` | 1–5 B | varint id of a value defined by an earlier `TEXT` frame |

### `NameRef`

| `NameRef` | Meaning |
|---|---|
| `0` | Literal follows; assign it the next id |
| `n > 0` | Dynamic id `n-1`, defined earlier in this document |

### Varint (LEB128, unsigned)

| Value range | Bytes | Layout |
|---|---|---|
| 0 – 127 | 1 | `0vvvvvvv` |
| 128 – 16 383 | 2 | `1vvvvvvv 0vvvvvvv` |
| 16 384 – 2²¹−1 | 3 | `1vvvvvvv 1vvvvvvv 0vvvvvvv` |
| … | up to 5 | High bit = continuation; 7 payload bits per byte, least-significant group first |

## No static table

There is no predefined vocabulary. Both sides start from an empty table on every document
and learn names as they go.

- **The document is fully self-describing.** Encoder and decoder share no registry, no
  schema, no version negotiation — only the format. Nothing to deploy in lockstep.
- **Unknown tags round-trip exactly.** A decoder that has never seen `<flurbleWidget>`
  reconstructs the name character-for-character, because the name is in the bytes.
- **Cost is self-correcting.** Each distinct name is paid for once, at first sight. A
  hundred `<line>` elements pay for `line` once; a hundred unique names pay for all
  hundred, but that document really is that diverse.

## Definitions are emitted at the point of first use

No prelude, no hoisting. The literal appears inside the first element that needs it, ahead
of that element's children, and every later occurrence is a one-byte reference.

Hoisting definitions to the front is not merely undesirable, it is **incompatible with
streaming**: a name deep in the tree is discovered only after its ancestors' headers have
already been written. Buffering the whole document to hoist them would reintroduce exactly
the memory cost the two-pass design exists to avoid.

Placing the literal at first use also makes the ordering invariant self-evident: the
defining occurrence is by construction the first in document order, so every reference is
necessarily preceded by its definition. Recursive elements fall out for free — `<a><a/></a>`
defines on the outer and references on the inner, because a parent's name is emitted before
descending.

**Ids are not on the wire.** Both sides assign them from the same counter in
first-occurrence document order, so the literal *is* the definition. That saves bytes and
removes a desync mode.

### Assign the id *before* descending into children

"Document order" means pre-order: a parent claims its id when its own literal is written,
ahead of its children. This is easy to violate by accident. A first draft of the reference
encoder for the example below built each node's children before the node itself — natural
in any language with eager argument evaluation — which assigned `line` id 0 and `order`
id 1, the reverse of document order.

The resulting stream looked healthy: every `Length` covered its children exactly, and a
structural walk validated cleanly. Only the *names* were wrong. The decoder assigns ids in
the order literals appear in the byte stream, so it read `order` as id 0, and the second
element's reference then resolved to `<order>` instead of `<line>`.

This is the encoder/decoder table desync in its purest form. Mutation testing against the
implementation pins down exactly which checks catch it:

| Check | Both passes consistently wrong | One pass wrong |
|---|---|---|
| Length arithmetic | passes | passes |
| Structural validation (every `Length` covers its children) | passes | passes |
| `bytes written == measured size` | **passes** — the passes agree with each other | catches it |
| Golden byte vector | catches it | passes |
| Round-trip to source | catches it | catches it |

The assertion only detects the two passes *disagreeing*; an encoder that is uniformly wrong
sails past it. Round-trip is the one check that catches both shapes, which is the argument
for making it the primary test rather than byte-count assertions.

### Why the literal is embedded rather than a separate `DEFINE` frame

| | Embedded (chosen) | Separate `DEFINE` frame |
|---|---|---|
| Cost per definition | `NameRef` + `NameLen` + name = 2 B + name | Type + Length + `NameLen` + name = 3 B + name, plus a third type in the registry |
| Element frames | Two shapes: literal or reference | One shape: always a reference |
| Definition ownership | Belongs to the element that introduced it | Free-floating in whatever parent's `Length` encloses it |
| Skipping a subtree | Unsafe unless names inside are still registered | Unsafe, and *easier to get wrong* — definitions look like independent records you could stride past |

The last row decides it. Under either scheme, a decoder skipping an unknown subtree must
still walk it to pick up definitions that later siblings may reference; the embedded form at
least makes that hazard visible in the structure.

## Interning text values

Tag names are not the only thing that repeats. Documents repeat *values* too — enums,
country codes, status flags, dates, booleans-as-text — so text is interned by the same
mechanism: the first occurrence carries the literal and assigns the next id, later
occurrences are a short reference.

### Why a separate type instead of a discriminator field

Names put a `NameRef` field inside every `ELEMENT`, because an element frame carries
children as well and cannot be split by type. Text has no such constraint: the value *is*
the whole payload, so a reference can be its own type code. That distinction is worth a
byte on every text node.

For a value of *L* bytes appearing *k* times:

| Design | Literal | Reference | Saving |
|---|---|---|---|
| `ValueRef` field inside `TEXT` | `2 + L + 1` | 3 | `L(k−1) − k` |
| Separate `TEXT_REF` type | `2 + L` — unchanged | 3 | `(k−1)(L−1)` |

The second is never worse than not interning at all, so a document with no repeated values
pays nothing. The first would tax every text node, including the unique ones, to fund a
feature they never use.

### The threshold

`(k−1)(L−1)` is positive from `L = 2`, exactly zero at `L = 1`, and **negative at `L = 0`** —
a 2-byte empty literal would be replaced by a 3-byte reference. So the encoder references
only values of 2 bytes or more.

That threshold lives entirely in the encoder. **The decoder registers every `TEXT` literal it
reads**, whether or not the encoder chose to reference it later, so ids stay in step without
the decoder knowing the rule. Changing the threshold is therefore not a format change.

### What it inherits

Value interning is the name table pointed at a second dictionary, so it inherits all three
of the same hazards:

- **Reset between passes** — the measuring pass populates the value table; the emit pass must
  start from empty or it will write references where literals were counted.
- **Skip-safety** — a decoder that skips a subtree misses the value definitions inside it,
  exactly as it would miss name definitions.
- **Canonical encoding** — the rule is *always reference a value already seen*, so one
  document has one encoding.

### `TEXT_REF` is compression, not identity

**A `TEXT_REF` asserts that two values are equal, never that they are the same object.**
It carries no identity semantics whatsoever. This is the same split CBOR makes between tag
25/256 `stringref` — a string table purely for compression — and tags 28/29
`sharable`/`shared reference`, which do preserve identity. Two different tags because they
mean two different things.

The distinction is invisible on the wire but decisive in a decoder:

| | Table keyed by | Asserts | Safe when |
|---|---|---|---|
| Content dedup (`TEXT_REF`) | value equality | "equal content, stored once" | values are immutable |
| Identity preservation | reference equality | "the same object appears twice" | always — it is information |

Sharing decoded strings is safe precisely because .NET strings are immutable, which is why
the runtime interns literals already. The decoder still builds a fresh node per occurrence
and shares only the underlying string.

Because the format says nothing about identity, **whether the decoder shares instances is a
decoder setting, not a wire concern** — `TlvDecoderOptions.ShareValueInstances`. Sharing is
the default: it is free, unobservable for immutable strings, and the right answer for RPC,
where values are transient data. Turning it off materialises a distinct instance per
occurrence for callers whose object model attaches meaning to reference identity. Either way
the same bytes decode to the same content, and re-encoding produces the same bytes.

Generalising this to mutable objects would not be safe. Collapsing two equal-but-distinct
objects into one changes behaviour the moment either is mutated; failing to preserve a
genuinely shared reference duplicates it and makes cycles unrepresentable. If identity ever
needs preserving, it must be a **separate type code with a table keyed on reference
equality** (`ReferenceEqualityComparer` in .NET), never this one. Reserving that distinction
now is what lets it be added later without a format break.

Two things to know before going there: cycles force two-phase construction — the decoder
must hand out a reference to an object it has not finished building, which is at odds with
immutable records — and back-references enable amplification, where a small document
referencing one large subtree repeatedly explodes on decode. Cap'n Proto's traversal limit
exists for that; a depth cap alone would not catch it.

### Nesting depth

Frame nesting is capped at **512 levels beneath the root**, and the cap is part of the
format rather than of either codec.

Both sides recurse once per frame, and a `StackOverflowException` cannot be caught — it
ends the process. The decoder therefore has to bound depth, because it reads bytes it did
not produce. The encoder enforces the identical bound, checked during the measuring pass so
that a rejected tree leaves the sink untouched rather than half-written.

Enforcing it on only one side is worse than not enforcing it at all: an unbounded encoder
produces documents its own decoder refuses, and the failure surfaces at the far end of the
wire rather than at the point the tree was built. That asymmetry existed here and was found
by a benchmark, not by a test — the depth-1000 row reported NA where every other row
reported a time.

The limit is not a defence against amplification. A shallow document can still reference one
large subtree repeatedly; bounding that needs a traversal budget, as noted above.

### Hashing

No cryptographic hash is needed, and no digest goes on the wire. A `Dictionary<string, int>`
already resolves hash collisions by comparing the actual strings, so interning is correct by
construction. Putting a SHA-256 digest on the wire instead would make a reference ~34 bytes
framed, which only breaks even above about 33 bytes — against 3 bytes for an ordinal.

The one case where a digest belongs on the wire is *cross-document* content addressing:
referencing values the decoder received in an earlier, unrelated message. That is a
different feature, and a stateful one.

## Worked example

`<order><line>a</line><line>b</line></order>` — 43 bytes of XML, 26 bytes encoded.

| Offset | Bytes | Field | Meaning |
|---|---|---|---|
| `00` | `01` | Type | ELEMENT |
| `01` | `18` | Length | 24 bytes of value |
| `02` | `00` | NameRef | literal follows |
| `03` | `05` | NameLen | 5 |
| `04` | `6F 72 64 65 72` | Name | `order` → **id 0** |
| `09` | `01` | Type | ELEMENT |
| `0A` | `09` | Length | 9 |
| `0B` | `00` | NameRef | literal follows |
| `0C` | `04` | NameLen | 4 |
| `0D` | `6C 69 6E 65` | Name | `line` → **id 1** |
| `11` | `02` | Type | TEXT |
| `12` | `01` | Length | 1 |
| `13` | `61` | Chars | `a` |
| `14` | `01` | Type | ELEMENT |
| `15` | `04` | Length | 4 |
| `16` | `02` | NameRef | id 1 → `line`, no literal |
| `17` | `02` | Type | TEXT |
| `18` | `01` | Length | 1 |
| `19` | `62` | Chars | `b` |

Flat: `01 18 00 05 6F 72 64 65 72 01 09 00 04 6C 69 6E 65 02 01 61 01 04 02 02 01 62`

The second `<line>` is 6 bytes against 11 for the first — the name collapsed to a single
`02`.

### Length arithmetic

| Node | Name cost | Children | Value `Length` | Frame total |
|---|---|---|---|---|
| `TEXT "a"` | — | — | 1 | 3 |
| `line` #1 | 1 + 1 + 4 = 6 | 3 | **9** | 11 |
| `TEXT "b"` | — | — | 1 | 3 |
| `line` #2 | 1 | 3 | **4** | 6 |
| `order` | 1 + 1 + 5 = 7 | 11 + 6 = 17 | **24** | 26 |

The two `line` rows are the crux: **identical XML subtrees, different encoded sizes.**

## Consequence for the two-pass encoder

This breaks an assumption from the companion note. The measuring pass is no longer pure
arithmetic over the tree — whether an element costs a literal or a reference depends on the
name table, so **the counting sink must build the same table the emit pass will build.**

And the trap: **the table must be reset to empty before the emit pass.** Carry pass 1's
populated table into pass 2 and every name is already known, so pass 2 writes references
where pass 1 measured literals, every length written is too large, and the document decodes
as truncated garbage.

Reset and replay. The `bytes written == measured size` assertion is exactly what catches
this class of bug, and it fails at the first mismatched element instead of producing a
silently corrupt stream.

## Open decision: table lifetime

| Scope | Behavior | Trade |
|---|---|---|
| **Per document** | Table starts empty at each root, dies at the end | Every message stands alone — decodable in isolation, reorderable, cacheable, retransmittable. Repeated vocabulary is re-paid per message |
| **Per connection** | Table persists across messages, as HPACK does on an HTTP/2 connection | Message #2 onward is dramatically smaller. But message *N* is undecodable without having processed 1…*N*−1 — no random access, no independent replay, and a dropped or reordered message corrupts everything after it |

Per-connection is a large win for chatty streams of similar documents and a serious
liability everywhere else. HPACK accepts that trade because HTTP/2 guarantees ordered,
lossless, single-connection delivery. **Default to per-document unless the transport offers
the same guarantee.**

## Bound the table

With no static table every name is dynamic, so a hostile document of a million distinct
one-character tags is a memory amplification attack. Cap entries and total name bytes; on
hitting the cap, stop adding and emit literals from then on.

Falling back to literals costs bytes but keeps both sides in step with no eviction policy to
get wrong — and eviction desync is the failure mode that yields silently *wrong names*
rather than an error.

## Rejected: hashing the name into a fixed-width tag

Tempting — `tag = hash32(name)`, fixed width, no table at all. Two killers:

1. **Collisions** need a resolution scheme that reintroduces the literal anyway.
2. **It is one-way.** A decoder can recognize names it already knows but cannot reconstruct
   one it does not, which directly violates the requirement that unknown tags round-trip.

Viable only as a dispatch accelerator alongside the literal, never as the sole
representation.

## Prior art

- **EXI** (W3C Efficient XML Interchange) — solves this with grammars plus string tables,
  including a schema-less mode for open vocabularies.
- **Fast Infoset** (ITU-T X.891) — binary XML built on index tables for names; simpler than
  EXI and closest to the scheme above.
- **HPACK / QPACK** (RFC 7541, RFC 9204) — the literal-with-indexing pattern and its
  security analysis, including the table-bomb concerns.

## Loose ends

- **Namespaces.** With no attributes there are no `xmlns` declarations, but if prefixed
  names like `<ns:tag>` can appear, decide whether an interned name is the raw QName or a
  resolved (URI, local) pair. Retrofitting this later is painful.
- **Canonical encoding.** If documents are ever signed, the same input must produce the same
  bytes. Fixing the encoder's choices — always reference when an id exists, never re-emit a
  literal for a known name — is what makes the encoding deterministic.
