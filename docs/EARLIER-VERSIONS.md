# Finding earlier versions

Releases are preserved by **git tags**, not by copies kept in the working tree.
(Until `0.3` the repository also kept frozen copies — `docs/LANGUAGE-0.1.md`,
`docs/LANGUAGE-POC.md`, `langspec/versions/<n>/`, `langspec/archived/` — but a tag is
an exact snapshot of the whole repository, so the copies only duplicated history and
risked being edited out from under it. The `0.3` PR that removed them is the last
place they appear in the tree.) Tag every release (`0.3.0`, ...).

Browse a whole version with `git checkout <tag>`, or on GitHub at
`.../tree/<tag>`. To read one file without checking anything out:

```
git show <tag>:<path>
```

| What you want | Tag | Path |
|---|---|---|
| `0.1-poc` language reference (the Python interpreter, `poc/`) | `0.1.3-poc` | `docs/LANGUAGE.md` |
| `0.1` language reference | `0.1.0` | `docs/LANGUAGE.md` |
| `0.1` grammar and README | `0.1.0` | `langspec/SYNTAX_GRAMMAR.md`, `langspec/README.md` |
| `0.2` language reference | `0.2.0` | `docs/LANGUAGE.md` |
| `0.2` grammar, README and examples | `0.2.0` | `langspec/SYNTAX_GRAMMAR.md`, `langspec/README.md`, `langspec/examples/` |
| `0.3` language reference | `0.3.0` | `docs/LANGUAGE.md` |
| `0.3` grammar, README and examples | `0.3.0` | `langspec/SYNTAX_GRAMMAR.md`, `langspec/README.md`, `langspec/examples/` |
| Pre-renumbering planning snapshots (formerly `langspec/archived/`; their "0.1"/"0.2"/"0.3" are *not* today's versions — see the renumbering note in `ROADMAP.md`) | `0.2.0` | `langspec/archived/` |

Example: `git show 0.2.0:docs/LANGUAGE.md`.

**What stays in the tree.** The implementation plans and decision logs
(`docs/PLAN-0.1-POC.md`, `PLAN-0.1.md`, `PLAN-0.2.md`, `PLAN-0.3.md`) are history in
their own right and remain. `langspec/versions/0.1/examples/` also remains, because CI's
`poc/` smoke test and the `poc/` release bundle read it. Older plan documents refer to
the removed files by their old paths; use the table above to find them.
