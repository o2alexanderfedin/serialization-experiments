# Contributing

This repository enforces [git-flow](https://nvie.com/posts/a-successful-git-branching-model/).
Solo workflow: no pull requests. Work happens on branches; `git flow ... finish` merges.

## One-time setup

```bash
git flow init -d              # main / develop, standard prefixes
./scripts/install-hooks.sh    # enables the versioned hooks in .githooks/
```

> **git-flow edition matters.** `bugfix/*` branches and several conveniences exist only in
> the maintained [AVH edition](https://github.com/petervanderdoes/gitflow-avh)
> (`brew install git-flow-avh`). The original nvie build (`git flow version` → `0.4.1`) has no
> `git flow bugfix` subcommand — create those branches manually with
> `git checkout develop && git checkout -b bugfix/<name>`. Both editions are accepted by the
> hooks and by `git-flow-guard`.

## Adding an experiment

```bash
git flow feature start msgpack-vs-cbor
mkdir -p experiments/msgpack-vs-cbor
# write experiments/msgpack-vs-cbor/README.md: hypothesis, method, results
git add . && git commit -m "feat(msgpack-vs-cbor): add throughput benchmark"
git flow feature finish msgpack-vs-cbor
git push origin develop
```

## Releasing

```bash
git flow release start 0.2.0
# bump versions, update CHANGELOG
git flow release finish 0.2.0
git push origin main develop --tags
```

## Branch rules

| Head branch  | Allowed base       |
|--------------|--------------------|
| `feature/*`  | `develop`          |
| `bugfix/*`   | `develop`          |
| `release/*`  | `main`, `develop`  |
| `hotfix/*`   | `main`, `develop`  |
| `support/*`  | `main`             |

Violations are rejected by the local hooks in `.githooks/`.
check on both protected branches.

## Commit messages

[Conventional Commits](https://www.conventionalcommits.org/):
`feat:`, `fix:`, `perf:`, `docs:`, `chore:`, `bench:`, `refactor:`.
Scope with the experiment name where it applies — `bench(cbor): ...`.
