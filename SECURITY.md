# Security

## Reporting a vulnerability

Please report security issues privately through GitHub's **Report a vulnerability** button on this
repository (Security tab), not in public issues. Include steps to reproduce and the cmux version
(`cmux version`).

## Security model

cmux runs shells as you, so anything that can talk to cmux can effectively run commands as you.
The design goal is that **only your own Windows account, on this machine,** can do that.

- **Control pipe** (`\\.\pipe\cmux`) and **daemon pipe** (`\\.\pipe\cmux-daemon`): created with a DACL
  that grants access only to your user SID. Clients connect with `CurrentUserOnly`, so they refuse
  a pipe created by another account (anti-squatting). Request size is capped.
- **Terminal output is untrusted.** Escape sequences can't make cmux run anything:
  - OSC 7 / OSC 9;9 working-directory reports must be local, fully qualified paths. UNC paths and
    remote hosts are rejected, so a malicious `cat` can't make cmux contact an SMB server.
  - Ctrl+click only opens `http://` and `https://` links.
  - OSC 52 (clipboard write) is not supported.
- **Child processes:** each shell runs in a Job Object, so closing a pane also ends the programs it
  started. ConPTY pipe handles are not inheritable, so other programs can't read or type into panes.
- **Session daemon** (opt-in): validates working directories and the executable path of new sessions,
  clamps terminal sizes, and drops clients that stop reading.
- **AI agent** (opt-in, off by default):
  - Shell commands, pane input, file writes, custom tools and MCP calls each need your approval.
    The prompt shows the exact command, and **Deny** is the default.
  - Terminal output, tool output and `AGENTS.md` are passed to the model as untrusted data, after
    secrets are redacted.
  - Model output is never typed into your shell.
  - API keys are stored with Windows DPAPI (current user). Base URLs must be `https://`
    (`http://` only for localhost).
- **Privacy:** terminal transcripts are **off by default**. Command logs redact common secrets
  (tokens, `Authorization` headers, `sk-…`, `ghp_…`, `AKIA…`, `PASSWORD=` and similar). Everything
  lives in `%LOCALAPPDATA%\cmux`. Diagnostic logging (`CMUX_DEBUG=1`) never records terminal
  content or command lines.

## Known limitations

- Release binaries are not code-signed yet. Verify the SHA-256 published with each release.
- Any program already running as your user can still drive cmux through its pipes. That isn't a
  privilege boundary on Windows, and the same is true of every terminal.
