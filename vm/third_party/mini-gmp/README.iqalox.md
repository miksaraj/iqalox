# mini-gmp (vendored)

`mini-gmp.c` and `mini-gmp.h` are copied **unmodified** from GNU MP 6.3.0
(`mini-gmp/` in the GMP source tree). They give `vm/` arbitrary-precision
integers for `0.4`'s integer type (`docs/PLAN-0.4.md` decisions 1-10). Values
that fit in 64 bits stay inline in `Value`. Only larger ones use mini-gmp.

mini-gmp is GMP's single-file subset. It is meant for numbers up to a few
hundred bits, where it is at most 10× slower than full GMP. It has no
asymptotically fast multiplication or division (see `README`).

| File | SHA-256 |
|---|---|
| `mini-gmp.c` | `378a9731eb6fd69b93fa074172f79be4089c813365613e414f4da4923dcbda52` |
| `mini-gmp.h` | `54ec9383fdf6103a0e22bde2663ca8eb420338015839b663e0eccee103afae99` |

The files were taken from Ubuntu's `gmp_6.3.0+dfsg.orig.tar.xz` (SHA-256
`bd2966e6d277f79328e894a5a9f3ba3fbf2ed2be81def5f48623e30c23fb1572`). Debian's
`+dfsg` repack only removes GMP's GFDL-licensed manual; `mini-gmp/` is as
upstream.

## License

mini-gmp is dual-licensed: LGPL v3 or later, or GPL v2 or later, at the
recipient's option (see the header of `mini-gmp.c`). Iqalox (GPL v3) uses it
under LGPL v3+. The license texts are `COPYING.LESSERv3`, `COPYINGv2` and
`COPYINGv3`. The VM build copies them into `third-party-licenses/` in its build
directory, and the release bundle ships that directory next to `iqaloxvm`.

## Updating

Replace the two files from a newer GMP release, update the version in
`vm/CMakeLists.txt` (`IQALOX_MINI_GMP_VERSION`), the hashes above and the
expected version in `vm/tests/test_dependencies.cpp`, then run the VM test
suite.
