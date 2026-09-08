# Pinned PTY descriptor boundary

The managed Porta.Pty 1.0.7 package is unchanged. Its MIT native shim is pinned to
`https://github.com/tomlm/Porta.Pty` commit
`54684ba55148ed6bcd0c827ad7e8841a3289a466`.

`upstream/porta_pty.c` SHA-256:
`9331e2dff6af7dfd553dba94602321e10e45760ef510b437a27e5afb55c4ec6f`.
`upstream/LICENSE` SHA-256:
`60957c2a4732512d8f4f86a8511181d435605a4872a1d4ec0456f6fe302d3ed5`.

The separate patch closes inherited descriptors only in the native forkpty child,
after its standard descriptors have been attached to the PTY. No additional handles
are explicitly passed by this API. Darwin closes through the pre-fork finite kernel
descriptor ceiling; Linux uses close_range with a pre-fork kernel nr_open ceiling
close-loop fallback for older kernels, also covering descriptors opened before a
hard-limit reduction. If a finite safe ceiling cannot be obtained, spawning fails.
Only close
and syscall execute in this added post-fork boundary. The existing upstream
environment/exec implementation is retained. Parent PTY masters are close-on-exec.

The distinct `libghostshell_pty` artifact exposes ABI marker
`ghostshell_pty_descriptor_boundary_abi` = 1. Unix startup never falls back to the
unpatched package native shim. Windows retains the unchanged package implementation.
