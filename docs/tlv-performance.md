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
```

**These are ShortRun numbers and several have an error bar wider than the mean** — the
`text-heavy`/100 `EncodeToArray` row reports 23.2 μs ± 225 μs. Treat the allocation columns
as solid (they are counted, not sampled) and the timings as indicative only. Re-run without
`--job short` before relying on any specific duration.

Document shapes, each 100 and 1000 elements:

| Shape | Structure |
|---|---|
| `repeated` | siblings all named `line` — the case interning is built for |
| `unique` | every element a distinct name — worst case for the table |
| `deep` | one chain, so recursion depth equals node count |
| `text-heavy` | repeated names with 200-character text, payload dominating structure |

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

| Shape | Size | Mean | Allocated |
|---|---|---:|---:|
| deep | 100 | 6.98 μs | 17.82 KB |
| deep | 1000 | **NA** | **NA** |
| repeated | 100 | 8.24 μs | 20.29 KB |
| repeated | 1000 | 63.75 μs | 196.08 KB |
| text-heavy | 100 | 11.82 μs | 57.80 KB |
| text-heavy | 1000 | 154.76 μs | 571.09 KB |
| unique | 100 | 8.69 μs | 26.23 KB |
| unique | 1000 | 83.52 μs | 251.24 KB |

## What held up

**The two-pass cost is roughly what the design predicted.** `MeasureOnly` runs at 0.27–0.47
of a full encode across every shape, so the emit pass costs about the same as the measuring
pass plus the write itself — the "~2× traversal CPU" estimate is sound. Measuring is
genuinely cheap: it does no I/O and, for text, only calls `GetByteCount`.

**Interning pays.** `repeated`/1000 encodes in 61 μs against 142 μs for `unique`/1000 — the
same node count, differing only in whether names collapse to a one-byte reference.

## Finding 1 — the emit pass allocates O(payload), contradicting the design

`text-heavy`/1000 produces roughly 207 KB of output. Writing it to a `CountingSink`, which
stores nothing at all, still allocates **314 KB**. The measuring pass over the same tree
allocates 32 KB.

The cause is in the emit pass:

```csharp
sink.Write(Encoding.UTF8.GetBytes(text.Value));   // a byte[] per text node
byte[] nameBytes = Encoding.UTF8.GetBytes(element.Name);   // and per literal name
```

Every string is transcoded into a freshly allocated array that is written once and dropped.
The measuring pass avoids this because `GetByteCount` allocates nothing — which is exactly
why its allocation ratio falls to 0.10 on the text-heavy shape.

So the design's claim holds in one sense and fails in another: the encoder never *holds* the
payload, but it does *allocate* it, as short-lived garbage. Peak live memory is O(depth) plus
the size cache; total allocation is O(payload).

Fix: transcode into a stack or pooled buffer via
`Encoding.UTF8.GetBytes(ReadOnlySpan<char>, Span<byte>)` instead of the array-returning
overload. The exact byte count is already known from the measuring pass.

## Finding 2 — `Encode(Node)` ignores the size it just computed

`EncodeToArray` allocates 997 KB for the same ~207 KB document — 3.18× the counter baseline,
and the only row in the run to touch Gen2 and the large object heap (Gen0, Gen1 and Gen2 all
report 181.64).

The cause is that `Encode(Node)` writes into a default `MemoryStream`, which doubles its
buffer as it grows and then copies once more in `ToArray()`. Every intermediate buffer past
85 KB lands on the LOH.

This is avoidable for free: the measuring pass has already computed the exact output length.
Allocating a single `byte[]` of precisely that size and writing into it removes the doubling,
the copy, and the LOH traffic in one change.

## Finding 3 — the encoder can produce documents its own decoder rejects

`Decode`/`deep`/1000 is the `NA` in the decode table. It did not run slowly; it threw.

The decoder caps nesting at 512 frames to bound stack usage. The encoder has no
corresponding limit, so a 1000-deep chain encodes without complaint and then fails to decode
with *"Nesting deeper than 512"*. Existing round-trip coverage used depth 300 and never
noticed.

`DepthLimitTests` now pins all three halves of this: the encoder accepts it, the decoder
rejects it, and round-trip still works just under the limit. The behaviour is deliberate on
the decoder's side, so the open question is which way to resolve the asymmetry — cap the
encoder to match, raise the decoder limit, or make the limit a documented parameter of the
format rather than an implementation detail.

## Not yet measured

- Two-pass against a buffer-and-copy encoder, which would quantify the memory win directly
  rather than inferring it.
- Deflate chained onto the output, and its interaction with skipping.
- Value interning.
- Documents large enough for the size cache (one `long` per node) to matter.
