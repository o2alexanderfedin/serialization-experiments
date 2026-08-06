# Contributing

This repository enforces [git-flow](https://nvie.com/posts/a-successful-git-branching-model/).
Nothing lands on `main` or `develop` except through a pull request.

## One-time setup

```bash
git flow init -d              # main / develop, standard prefixes
./scripts/install-hooks.sh    # enables the versioned hooks in .githooks/
```

## Adding an experiment

```bash
git flow feature start msgpack-vs-cbor
mkdir -p experiments/msgpack-vs-cbor
# write experiments/msgpack-vs-cbor/README.md: hypothesis, method, results
git add . && git commit -m "feat(msgpack-vs-cbor): add throughput benchmark"
git flow feature publish msgpack-vs-cbor
gh pr create --base develop --fill
```

`git flow feature finish` merges locally into `develop` — the pre-push hook will then
refuse the push, because `develop` only accepts pull requests. Use
`git flow feature publish` + a PR instead.

## Releasing

```bash
git flow release start 0.2.0
# bump versions, update CHANGELOG
git push -u origin release/0.2.0
gh pr create --base main --fill      # merge to main, then tag
gh pr create --base develop --fill   # back-merge into develop
```

## Branch rules

| Head branch  | Allowed base       |
|--------------|--------------------|
| `feature/*`  | `develop`          |
| `bugfix/*`   | `develop`          |
| `release/*`  | `main`, `develop`  |
| `hotfix/*`   | `main`, `develop`  |
| `support/*`  | `main`             |

Violations are rejected by the `git-flow-guard` workflow, which is a required status
check on both protected branches.

## Commit messages

[Conventional Commits](https://www.conventionalcommits.org/):
`feat:`, `fix:`, `perf:`, `docs:`, `chore:`, `bench:`, `refactor:`.
Scope with the experiment name where it applies — `bench(cbor): ...`.
