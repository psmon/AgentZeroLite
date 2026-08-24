# Remote web client — xterm vendor folder

The remote terminal UI renders with [xterm.js](https://xtermjs.org/). For a normal LAN
deployment the client auto-loads xterm from jsDelivr when these files are absent, so nothing
here is required out of the box.

For **offline / air-gapped** deployments, drop the pinned assets here so the client never
reaches the network:

```
vendor/
  xterm.js              # @xterm/xterm@5.5.0  lib/xterm.js
  xterm.css             # @xterm/xterm@5.5.0  css/xterm.css
  xterm-addon-fit.js    # @xterm/addon-fit@0.10.0  lib/addon-fit.js
```

Fetch them once from a machine with connectivity:

```
curl -Lo xterm.js           https://cdn.jsdelivr.net/npm/@xterm/xterm@5.5.0/lib/xterm.js
curl -Lo xterm.css          https://cdn.jsdelivr.net/npm/@xterm/xterm@5.5.0/css/xterm.css
curl -Lo xterm-addon-fit.js https://cdn.jsdelivr.net/npm/@xterm/addon-fit@0.10.0/lib/addon-fit.js
```

`Wasm\**\*` in `AgentZeroWpf.csproj` copies whatever is present here next to the exe, so a
vendored file ships automatically on the next build.
