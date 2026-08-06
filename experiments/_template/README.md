# <experiment-name>

Copy this directory to `experiments/<experiment-name>/` and fill it in.

## Hypothesis

What do you expect, and why? State it as a falsifiable claim, e.g.
"CBOR encodes our event payload in ≥20% fewer bytes than JSON at equal decode throughput."

## Method

- **Data set** — shape and provenance of the payloads (record count, nesting depth, field types).
- **Candidates** — formats and library versions under test.
- **Harness** — how to reproduce: exact command, warmup, iteration count.
- **Environment** — CPU, RAM, OS, runtime/compiler version.

## Results

| Format | Encode (MB/s) | Decode (MB/s) | Payload (bytes) | Allocations |
|--------|---------------|---------------|-----------------|-------------|
|        |               |               |                 |             |

## Conclusion

What does this mean for format selection? Note where the result does *not* generalize.
