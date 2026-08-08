# Experiments

Two shapes live here. Pick whichever fits the work; both start with
`git flow feature start <experiment-name>`.

## Self-contained experiment

A single directory, no build required. Copy [`_template/`](_template/) and fill in its
`README.md`:

1. **Hypothesis** — what you expect and why.
2. **Method** — data set, payload shapes, hardware, how to reproduce.
3. **Results** — numbers (encode/decode throughput, payload bytes, alloc counts).
4. **Conclusion** — what it means for format selection.

## Language workspace

When an experiment needs a real implementation, a test suite, and a benchmark harness, it
gets a workspace named for its language rather than for a single hypothesis, because
several experiments share it.

- [`csharp/`](csharp/) — .NET solution: `src/` implementation, `tests/` xUnit,
  `bench/` BenchmarkDotNet.

Workspaces carry no results README of their own. The hypothesis, method, and numbers go in
`../docs/` instead, so a note can span more than one experiment — the TLV codec's wire
format, design rationale, and measurements each have their own file there.
