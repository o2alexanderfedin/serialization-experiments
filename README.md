# serialization-experiments

A sandbox for experiments with serialization formats, codecs, and schema evolution
strategies — benchmarking throughput, payload size, and ergonomics across candidates
(JSON, MessagePack, CBOR, Protobuf, FlatBuffers, Cap'n Proto, Avro, Bincode, …).

Each experiment is self-contained under `experiments/<name>/` with its own README
stating the hypothesis, method, and results.

## Layout

```
csharp/               # C# solution — the harness experiments are written against
  src/SerializationExperiments/         # ISerializer + one class per candidate format
  tests/SerializationExperiments.Tests/ # round-trip / correctness contract per candidate
experiments/          # one directory per experiment, self-contained
  <name>/
    README.md         # hypothesis, method, results
docs/                 # cross-experiment notes and comparisons
```

Language harnesses live at the top level (`csharp/`, and siblings later) because a
solution spans many experiments; `experiments/<name>/` holds the write-up for a single
question and points at the code that answers it.

## C#

Requires the .NET 10 SDK (pinned in `csharp/global.json`).

```bash
cd csharp
dotnet build      # warnings are errors
dotnet test
```

`ISerializer` is the seam every candidate implements, so measurement code never depends
on which library is underneath. `SystemTextJsonSerializer` is the in-the-box baseline any
alternative has to beat to justify its dependency.

## Setup

```bash
git clone https://github.com/o2alexanderfedin/serialization-experiments.git
cd serialization-experiments
git flow init -d          # aligns local config with main/develop
```

## Development

This project uses **git-flow**. Direct commits to `main` and `develop` are blocked
locally by a pre-commit hook and remotely by branch protection rules.

**Feature branches** (branch from & merge to `develop`):

```bash
git flow feature start my-experiment    # creates feature/my-experiment
# ...work, commit...
git flow feature publish my-experiment  # push and open a PR into develop
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

Git-flow is enforced at three layers:

1. **Local `pre-commit` hook** — rejects commits made directly on `main` / `develop`.
2. **Local `pre-push` hook** — rejects pushes to `main` / `develop` and pushes of
   branches that violate git-flow naming.
3. **GitHub** — branch protection on `main` and `develop` (pull request required,
   force-push and deletion blocked) plus the `git-flow-guard` Actions workflow, which
   fails any PR whose head/base pair is not a legal git-flow transition.

Run `./scripts/install-hooks.sh` after cloning to install the local hooks (they live in
`.githooks/` and are versioned; the script points `core.hooksPath` at them).

## License

MIT — see [LICENSE](LICENSE).
