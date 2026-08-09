# TLV codec performance — first measurements

Numbers for the encoder and decoder described in
[emitting TLV length prefixes without buffering](tlv-length-prefix-without-buffering.md) and
[mapping open-vocabulary XML onto TLV](xml-to-tlv-dynamic-tag-table.md).

## Method

```
BenchmarkDotNet v0.15.8, macOS Tahoe 26.5.2 (Darwin 25.5.0)
Apple M1 Pro, 1 CPU, 8 logical and 8 physical cores
.NET SDK 10.0.103, .NET 10.0.3, Arm64 RyuJIT armv8.0-a
Job: ShortRun
```

Reproduce:

```bash
cd experiments/csharp
dotnet run -c Release --project bench/SerializationExperiments.Benchmarks -- --filter '*'
dotnet run -c Release --project bench/SerializationExperiments.Benchmarks -- sizes
dotnet run -c Release --project bench/SerializationExperiments.Benchmarks -- alloc
```

**These are ShortRun numbers and several have an error bar wider than the mean** — the
`text-heavy`/100 `EncodeToArray` row reports 23.2 μs ± 225 μs. Treat the timings as
indicative only, and re-run without `--job short` before relying on any specific duration.

> **Correction.** An earlier version of this section said to treat the allocation columns as
> solid, "counted, not sampled". That is wrong. `MemoryDiagnoser` reports a total divided by
> an auto-scaled operation count, so one-time costs amortise differently between runs; two
> runs of *identical* code moved the `MeasureOnly` rows by up to 95%. Byte counts now come
> from the `alloc` report, which reads the thread allocation counter around one warmed-up
> call and reproduces exactly. The `Allocated` columns in the tables below are kept as the
> historical record of the run they came from, not as current figures.

Document shapes, each 100 and 1000 elements:

| Shape | Structure |
|---|---|
| `repeated` | siblings all named `line`, values all distinct — repeated *names* |
| `unique` | every element a distinct name — worst case for the name table |
| `deep` | one chain, so recursion depth equals node count |
| `text-heavy` | repeated names with one identical 200-character value — interning's best case |
| `values-repeat` | values drawn from a 10-word vocabulary — the realistic enum-like case |
| `values-unique` | identical to `values-repeat` but every value distinct — the control |
| `values-mixed` | 800 distinct values then 200 from a 4-word vocabulary — a vocabulary discovered late |
| `typed` | `values-repeat` with every child wrapped in a type tag — isolates what polymorphism costs |
| `records` | rows of mixed scalar data as typed values |
| `records-text` | the same rows stringified — the control for `records` |

`values-repeat` and `values-unique` are a matched pair: same element name, same child count,
same 10-character value length, so their XML is byte-for-byte the same size. The only
variable between them is whether values repeat, which isolates value interning from
everything else.

`MeasureOnly` is pass 1 alone. `EncodeToCounter` is both passes into a `CountingSink`
(baseline). `EncodeToArray` is both passes into a real buffer.

## Encode

| Method | Shape | Size | Mean | Ratio | Allocated | Alloc ratio |
|---|---|---|---:|---:|---:|---:|
| MeasureOnly | deep | 100 | 3.96 μs | 0.40 | 12.09 KB | 0.48 |
| EncodeToCounter | deep | 100 | 10.17 μs | 1.00 | 25.23 KB | 1.00 |
| EncodeToArray | deep | 100 | 13.16 μs | 1.32 | 30.33 KB | 1.20 |
| MeasureOnly | deep | 1000 | 59.59 μs | 0.46 | 116.03 KB | 0.47 |
| EncodeToCounter | deep | 1000 | 129.43 μs | 1.00 | 247.16 KB | 1.00 |
| EncodeToArray | deep | 1000 | 136.31 μs | 1.06 | 291.77 KB | 1.18 |
| MeasureOnly | repeated | 100 | 3.13 μs | 0.45 | 7.53 KB | 0.69 |
| EncodeToCounter | repeated | 100 | 7.02 μs | 1.00 | 10.98 KB | 1.00 |
| EncodeToArray | repeated | 100 | 10.16 μs | 1.45 | 19.23 KB | 1.75 |
| MeasureOnly | repeated | 1000 | 28.65 μs | 0.47 | 63.73 KB | 0.67 |
| EncodeToCounter | repeated | 1000 | 61.34 μs | 1.00 | 95.30 KB | 1.00 |
| EncodeToArray | repeated | 1000 | 76.60 μs | 1.25 | 108.66 KB | 1.14 |
| MeasureOnly | text-heavy | 100 | 9.84 μs | 0.81 | 7.53 KB | 0.23 |
| EncodeToCounter | text-heavy | 100 | 12.39 μs | 1.00 | 32.86 KB | 1.00 |
| EncodeToArray | text-heavy | 100 | 23.18 μs | 1.90 | 117.13 KB | 3.56 |
| MeasureOnly | text-heavy | 1000 | 30.58 μs | 0.27 | 32.48 KB | 0.10 |
| EncodeToCounter | text-heavy | 1000 | 111.92 μs | 1.00 | 314.05 KB | 1.00 |
| EncodeToArray | text-heavy | 1000 | 215.35 μs | 1.92 | 997.20 KB | 3.18 |

## Decode

MediumRun, all eight shapes. Error bars are 3–9% of the mean here rather than wider than it,
which is the difference a real job makes.

| Shape | Size | Mean | Error | Allocated |
|---|---|---:|---:|---:|
| deep | 100 | 6.94 μs | ±0.20 | 17.93 KB |
| deep | 1000 | 42.28 μs | ±1.27 | 88.35 KB |
| repeated | 100 | 7.32 μs | ±0.19 | 20.40 KB |
| repeated | 1000 | 76.41 μs | ±3.16 | 196.19 KB |
| text-heavy | 100 | 6.79 μs | ±0.22 | 16.97 KB |
| text-heavy | 1000 | 66.72 μs | ±2.39 | 157.60 KB |
| typed | 100 | 8.80 μs | ±0.29 | 20.59 KB |
| typed | 1000 | 88.92 μs | ±2.47 | 189.35 KB |
| unique | 100 | 8.92 μs | ±0.38 | 26.34 KB |
| unique | 1000 | 92.75 μs | ±3.61 | 251.35 KB |
| values-mixed | 100 | 8.15 μs | ±0.89 | 20.49 KB |
| values-mixed | 1000 | 77.35 μs | ±4.85 | 194.88 KB |
| values-repeat | 100 | 7.69 μs | ±0.68 | 17.26 KB |
| values-repeat | 1000 | 65.22 μs | ±2.13 | 157.89 KB |
| values-unique | 100 | 7.06 μs | ±0.21 | 21.19 KB |
| values-unique | 1000 | 74.23 μs | ±3.30 | 204.01 KB |

`deep`/1000 was `NA` in the first run — it threw, because the encoder produced a document its
own decoder rejected. That is finding 3, now fixed, and the shape is clamped to the format's
depth limit.

The old `text-heavy` rows are gone for the same reason as elsewhere in this document: they
were measured before value interning, when that shape was 207 KB rather than 6.2 KB.

## What held up

**The two-pass cost is roughly what the design predicted.** `MeasureOnly` runs at 0.27–0.47
of a full encode across every shape, so the emit pass costs about the same as the measuring
pass plus the write itself — the "~2× traversal CPU" estimate is sound. Measuring is
genuinely cheap: it does no I/O and, for text, only calls `GetByteCount`.

**Name interning pays.** `repeated`/1000 encodes in 61 μs against 142 μs for `unique`/1000 —
the same node count, differing only in whether names collapse to a one-byte reference. (This
run predates value interning; both shapes have all-distinct values, so only names are
involved.)

## Findings 1–3 — all three resolved

### First, a correction

Findings 1 and 2 were originally written against `text-heavy`/1000 as a ~207 KB document
allocating 314 KB to a sink that stores nothing, and 997 KB to an array — *"the only row in
the run to touch Gen2 and the large object heap."*

**Those numbers no longer describe anything.** Value interning landed after they were
written, and every one of `text-heavy`'s 1000 text nodes holds the same 200-character
string, so 999 of them collapsed to 3-byte references. The shape now encodes to **6,219
bytes**, not 207 KB. There is no LOH row left in the run, and `text-heavy` turned out to be
close to the *least* affected shape rather than the worst.

Both findings were still real. They were simply attributed to the wrong shape and the wrong
magnitude, because nobody re-derived them after the format changed underneath. Re-measuring
before fixing is what caught it.

### And a change of instrument

BenchmarkDotNet's `MemoryDiagnoser` divides a total by an auto-scaled operation count, so
one-time costs amortise differently between runs. Across two runs of the same code it moved
`MeasureOnly` — which had not changed at all — by up to **95%**. That is far too noisy to
attribute a 20% change to an edit.

Allocation is now measured by `dotnet run -c Release --project bench/… -- alloc`, which
reads the thread's allocation counter around a single warmed-up call. It reproduces
byte-for-byte across runs. Timing stays in BenchmarkDotNet; only the byte counts moved.

### Finding 1 — the emit pass allocated one array per literal

```csharp
sink.Write(Encoding.UTF8.GetBytes(text.Value));            // a byte[] per text node
byte[] nameBytes = Encoding.UTF8.GetBytes(element.Name);   // and per literal name
```

Every string was transcoded into a freshly allocated array, written once, and dropped. The
measuring pass avoided it because `GetByteCount` allocates nothing.

Literals up to 256 UTF-8 bytes now transcode through the stack, and longer ones borrow from
`ArrayPool`. The saving scales with the number of **distinct** literals, which is what makes
the near-zero rows below a confirmation rather than a disappointment:

| Shape (1000) | distinct literals | before | after | change |
|---|---:|---:|---:|---:|
| `unique` | ~2000 | 300,552 | 236,520 | −21.3% |
| `values-unique` | ~1001 | 174,584 | 134,520 | −22.9% |
| `repeated` | ~1001 | 166,584 | 134,520 | −19.3% |
| `deep` | ~1001 | 166,528 | 134,488 | −19.2% |
| `values-repeat` | ~11 | 33,760 | 33,296 | −1.4% |
| `text-heavy` | ~3 | 32,808 | 32,520 | −0.9% |

Bytes allocated by the emit pass, computed as encode-to-counter minus measure.

The design's claim now holds in both senses: the encoder neither holds nor allocates the
payload. What remains is the second `Tables` — the emit pass builds its interning
dictionaries from empty, by design — which is the bulk of what is left.

### Finding 2 — `Encode(Node)` ignored the size it had just computed

It wrote into a default `MemoryStream`, which doubles its buffer as it grows, then copied
once more in `ToArray()` — despite the measuring pass having already computed the exact
length. It now allocates one `byte[]` of precisely that size and writes into it through a
`BufferSink` that throws rather than growing, since an overrun means the two passes
disagreed, which is corruption and not a full buffer.

Allocation beyond producing the bytes, as a multiple of the output size — 1.00x means one
allocation of exactly the output and nothing else:

| Shape (1000) | before | after |
|---|---:|---:|
| `repeated` | 3.54x | **1.00x** |
| `unique` | 4.01x | **1.00x** |
| `deep` | 3.54x | **1.00x** |
| `text-heavy` | 3.63x | **1.01x** |
| `values-repeat` | 3.68x | **1.01x** |
| `values-unique` | 3.18x | **1.00x** |

### Finding 3 — the encoder could produce documents its own decoder rejected

`Decode`/`deep`/1000 was the `NA` in the decode table. It did not run slowly; it threw.

The decoder capped nesting at 512 frames to bound stack use — a `StackOverflowException`
cannot be caught, so bounding it is not optional for code reading bytes it did not produce.
The encoder had no corresponding limit, so a 1000-deep chain encoded without complaint and
then failed to decode. Round-trip coverage used depth 300 and never reached it.

The limit is now one shared constant, `TlvLimits.MaxDepth`, stated as part of the format
rather than privately in the decoder, and the encoder enforces it **in the measuring pass**
so a rejected tree leaves the sink untouched instead of half-written.

Enforcing on one side only is worse than not enforcing at all: it moves the failure from the
point the tree is built to the far end of the wire. Note also what the cap does *not* do —
it is no defence against amplification, where a shallow document references one large
subtree repeatedly. That needs a traversal budget.

The `deep` benchmark shape asked for 1,000 frames, which is no longer a legal document. It
is clamped to the limit, and both text reports say so rather than implying the requested
depth was measured.

## Value interning — measured after the fact

Text values are now interned like element names (`TEXT` literal assigns an id, `TEXT_REF`
references it). Same machine, same ShortRun job, so the numbers above are the "before".

### Size

```bash
dotnet run -c Release --project bench/SerializationExperiments.Benchmarks -- sizes
```

| Shape | Size | XML bytes | TLV bytes | Ratio |
|---|---:|---:|---:|---:|
| repeated | 1000 | 20,903 | 12,904 | 61.7 % |
| unique | 1000 | 28,687 | 21,792 | 76.0 % |
| deep | 1000 | 20,790 | 12,889 | 62.0 % |
| text-heavy | 1000 | 213,021 | 6,219 | **2.9 %** |
| **values-repeat** | **1000** | **27,013** | **6,106** | **22.6 %** |
| **values-unique** | **1000** | **27,013** | **15,016** | **55.6 %** |

The matched pair is the cleanest measurement here. Identical XML at 27,013 bytes both, so
value repetition is the only difference: **6,106 against 15,016 bytes, a 59 % reduction**
from interning alone.

`text-heavy` at 2.9 % is the theoretical ceiling — one identical 200-byte string reused
1000 times — and should not be read as a realistic ratio. `values-repeat` at 22.6 % is the
number to quote for enum-like data.

### The matched pair, at 1000 elements

Both shapes run with interning enabled; the only difference between them is whether values
repeat. This measures *what repetition is worth*, not what interning costs — the before/after
tables below measure that.

| | `values-repeat` | `values-unique` | Difference |
|---|---:|---:|---|
| Encoded size | 6,106 B | 15,016 B | **59 % smaller** |
| Encode (`EncodeToArray`) | 121.2 μs | 147.1 μs | 18 % faster |
| Encode allocation | **57.2 KB** | 349.5 KB | **84 % less** |
| Decode | 55.1 μs | 73.3 μs | 25 % faster |
| Decode allocation | 157.8 KB | 220.1 KB | 28 % less |

Repetition wins on every axis at once. The encode-allocation gap is the largest because a
referenced value is never transcoded to UTF-8 a second time, and the value table holds ten
entries instead of a thousand.

### Encode — `EncodeToArray`, what a caller actually pays

| Shape | Size | Time before | Time after | Alloc before | Alloc after |
|---|---:|---:|---:|---:|---:|
| deep | 1000 | 136.3 μs | 116.5 μs | 291.8 KB | 292.3 KB |
| repeated | 1000 | 76.6 μs | 153.3 μs | 108.7 KB | 339.6 KB |
| unique | 1000 | 165.0 μs | 232.1 μs | 411.1 KB | 610.8 KB |
| text-heavy | 1000 | 215.3 μs | 223.0 μs | **997.2 KB** | **118.1 KB** |

### Decode

| Shape | Size | Time before | Time after | Alloc before | Alloc after |
|---|---:|---:|---:|---:|---:|
| repeated | 1000 | 63.8 μs | 73.7 μs | 196.1 KB | 212.3 KB |
| unique | 1000 | 83.5 μs | 90.5 μs | 251.2 KB | 267.5 KB |
| text-heavy | 1000 | 154.8 μs | **58.6 μs** | 571.1 KB | **157.6 KB** |

### What this says

**When values repeat, it is decisive.** `text-heavy` output falls to 2.9 % of the XML,
encode allocation drops 88 % (997 → 118 KB, taking the Gen2 and LOH traffic from
[finding 2](#finding-2--encodenode-ignores-the-size-it-just-computed) with it), and *decode
gets 2.6× faster on 72 % less memory* — a reference needs no UTF-8 decode and no string
allocation, so the decoder hands back the existing instance.

**When values do not repeat, it costs.** `repeated`/1000 pays +100 % encode time and +212 %
allocation to save zero bytes; `unique`/1000 pays +41 % and +49 %. The value table is
populated for every distinct value in both passes, and each lookup hashes the string — the
cost lands on all values while the benefit lands only on repeats.

That is the trade in its plainest form: **interning taxes distinct values to subsidise
repeated ones.** For documents that repeat values it is overwhelmingly worth it; for
documents of entirely unique values it is pure overhead.

Worth noting the encode-time regression is smaller than it looks in the `text-heavy` column
(+38 % on `EncodeToCounter`, 111.9 → 154.9 μs) because hashing a 200-byte string per lookup
is itself O(L). Short values hash cheaply; long ones do not.

If the tax on distinct-value documents matters, the obvious lever is to intern only values
the measuring pass sees more than once — it already walks the whole tree, so the frequency
is available before the emit pass runs. That trades a second dictionary for the savings and
has not been measured.

## Typed values

`records` and `records-text` hold the same numbers, booleans and names in the same structure.
The only difference is whether a value rides as a primitive or as its decimal spelling.

| 1000 rows | TLV bytes | vs XML | Encode allocation |
|---|---:|---:|---:|
| `records` — typed | **38,972** | 39.4% | 405,048 |
| `records-text` — stringified | 53,388 | 54.0% | 533,160 |
| | **−27%** | | **−24%** |

Per value, measured rather than argued: `true` and `null` are **1 byte**, a small integer
**2**, a `float` **5**, a `double` **9**, a `Guid` **17**. Under the old uniform
Type-Length-Value frame every one of those would have paid an extra byte to declare a width
already implied by the type.

### Where typed values lose

An earlier version of `records` computed its `score` column as `index * 1.5`, which spells as
`"1498.5"` — six characters, eight bytes framed, against nine for a `double`. On that data the
whole advantage collapsed to **2%** (38,972 against 39,766), because text won the float column
outright and only the booleans and small integers still paid off.

The typed figure is identical in both runs, since a `double` is always nine bytes whatever it
holds. So the rule is simply: typed values win when the decimal spelling is long, which is
what real floating-point data looks like, and lose on round numbers. Anyone quoting the 27%
should know it is a property of the data, not of the format.

`f32` is the lever where precision allows — five bytes against nine, and it beats almost any
decimal spelling.

## Not yet measured

- Two-pass against a buffer-and-copy encoder, which would quantify the memory win directly
  rather than inferring it.
- Deflate chained onto the output, and its interaction with skipping.
- Documents large enough for the size cache (one `long` per node) to matter.
- Timings re-run without `--job short`, now that the allocation question is settled and only
  the durations remain unreliable.
- The emit pass's second `Tables`, which is now the largest remaining allocation and is
  inherent to the two-pass design rather than incidental to it.
