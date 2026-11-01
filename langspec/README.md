# `langspec/` — the Iqalox language specification

This directory holds the **language specification**: the syntax grammar, a directory
README (this file) explaining how to navigate the spec itself, and runnable example
programs — kept independent of any one implementation (`poc/`, `compiler/`+`vm/`). See
the root `README.md` for the lexical grammar and precedence table, `../ROADMAP.md` for
the full version plan, and `../CLAUDE.md` for the repo-wide conventions this directory
follows.

## Directory layout

```
langspec/
├── README.md            this file
├── SYNTAX_GRAMMAR.md     current active-target version's grammar
├── examples/             current active-target version's example programs
└── versions/0.1/examples/  the 0.1 examples, kept only because CI reads them (see below)
```

- **Everything here is the current active target** — right now, `0.3`
  (`../docs/PLAN-0.3.md`). "Active target" means this is what the language is being
  designed and built *towards*, not necessarily what's fully implemented yet:
  `compiler/`+`vm/` land `0.3`'s features incrementally across several phases, and
  `../docs/PLAN-0.3.md` §4's feature checklist tracks exactly what's landed so far.
  Until every phase lands, the top-level `examples/` may describe the target spec and
  not yet run against `compiler/`+`vm/` — see `../CLAUDE.md`'s "Example scripts"
  convention for how this is tracked during the transition.
- **Earlier versions are not kept as copies in this directory.** Each release is
  preserved by a git tag, which is an exact snapshot of the whole repository (this
  directory included). `../docs/EARLIER-VERSIONS.md` lists which tag and path holds
  each earlier grammar, README, example set and language reference — including the
  pre-renumbering planning-era snapshots that used to live in `archived/`, whose
  "`0.1`/`0.2`/`0.3`" are *not* today's versions of the same name (read
  `../ROADMAP.md`'s renumbering note first).
- **`versions/0.1/examples/`** is the one exception: the `0.1` example programs stay in
  the tree because tooling reads them — CI's `poc/` smoke test runs them against the
  frozen Python interpreter, and the `poc/` release archive bundles them.
- **`examples/`** holds cross-implementation conformance fixtures: the same `.iqx`
  source, expected to produce the same output regardless of which implementation runs
  it (where more than one implementation can run it at all — see `../CLAUDE.md`). Its
  `modules/` subdirectory is a multi-file program: compile `modules/main.iqx` and the
  compiler follows its `import`s.

## Finding what you're looking for

- **"What does Iqalox do today?"** → `../docs/LANGUAGE.md` (the full prose reference,
  with what is new in `0.3` marked); the terser grammar-level equivalent is this
  directory's `SYNTAX_GRAMMAR.md` and `examples/`. Note the grammar and examples describe
  `0.3`, the *target*, and may be a little ahead of what has shipped.
- **"What's being planned/built right now?"** → `../docs/PLAN-0.3.md` for the design
  decisions, open questions, and implementation status behind `0.3`.
- **"What did an earlier version look like?"** → `../docs/EARLIER-VERSIONS.md`.
