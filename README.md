# serialization-experiments

A sandbox for experiments with serialization formats, codecs, and schema evolution
strategies — benchmarking throughput, payload size, and ergonomics across candidates
(JSON, MessagePack, CBOR, Protobuf, FlatBuffers, Cap'n Proto, Avro, Bincode, …).

Experiments live under `experiments/`, in one of two shapes. A self-contained experiment
gets its own directory with a README stating the hypothesis, method, and results — copy
`experiments/_template/` to start one. Work that needs a real build, test suite, and
benchmark harness instead gets a language workspace, such as `experiments/csharp/`, whose
write-ups live in `docs/`.

## Layout

```
experiments/
  _template/          # copy this to start a self-contained experiment
    README.md         # hypothesis, method, results
  csharp/             # .NET workspace shared by the C# experiments
    SerializationExperiments.sln
    src/              # implementation
    tests/            # xUnit tests
    bench/            # BenchmarkDotNet harness
docs/                 # design notes, wire-format specs, and measured results
```

The TLV codec currently under `experiments/csharp/` is documented in
[`docs/xml-to-tlv-dynamic-tag-table.md`](docs/xml-to-tlv-dynamic-tag-table.md) (wire
format), [`docs/tlv-length-prefix-without-buffering.md`](docs/tlv-length-prefix-without-buffering.md)
(why encoding is two passes), and [`docs/tlv-performance.md`](docs/tlv-performance.md)
(numbers).

## Building the C# workspace

```bash
cd experiments/csharp
dotnet build SerializationExperiments.sln
dotnet test SerializationExperiments.sln
dotnet run -c Release --project bench/SerializationExperiments.Benchmarks -- --filter '*' --job short
```

## Setup

```bash
git clone https://github.com/o2alexanderfedin/serialization-experiments.git
cd serialization-experiments
git flow init -d          # aligns local config with main/develop
```

## Development

This project uses **git-flow**. Direct commits to `main` and `develop` are blocked by a pre-commit hook.

**Feature branches** (branch from & merge to `develop`):

```bash
git flow feature start my-experiment    # creates feature/my-experiment
# ...work, commit...
git flow feature finish my-experiment   # merges into develop
```

**Release branches** (branch from `develop`, merge to `main` + `develop`):

```bash
git flow release start 1.0.0
git flow release finish 1.0.0
```

**Hotfix branches** (branch from `main`, merge to `main` + `develop`):

```bash
git flow hotfix start 1.0.1
git flow hotfix finish 1.0.1
```

**Bugfix branches** (branch from & merge to `develop`):

```bash
git flow bugfix start my-fix
```

## Branch Model

| Branch       | Purpose                  | Merges into        |
|--------------|--------------------------|--------------------|
| `main`       | Production / tagged releases | —              |
| `develop`    | Integration branch (default) | —              |
| `feature/*`  | New experiments & features   | `develop`      |
| `bugfix/*`   | Fixes on develop             | `develop`      |
| `release/*`  | Release preparation          | `main`, `develop` |
| `hotfix/*`   | Urgent production fixes      | `main`, `develop` |

## Enforcement

Git-flow is enforced by versioned hooks in `.githooks/`:

1. **`pre-commit`** — rejects commits made directly on `main` / `develop`.
2. **`pre-push`** — rejects branches that violate git-flow naming.

On GitHub, `main` and `develop` block force-pushes and deletion.


Run `./scripts/install-hooks.sh` after cloning to install the local hooks (they live in
`.githooks/` and are versioned; the script points `core.hooksPath` at them).

## License

MIT — see [LICENSE](LICENSE).
