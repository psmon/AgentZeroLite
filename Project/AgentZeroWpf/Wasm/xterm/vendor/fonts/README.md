# JetBrains Mono

`JetBrainsMono-{Regular,Bold,Italic}.ttf` — the terminal's default typeface.

Copyright 2020 The JetBrains Mono Project Authors, licensed under the
**SIL Open Font License 1.1**. The full licence is in [`OFL.txt`](OFL.txt),
which must travel with these files: AgentZero Lite ships them inside the
installer and the release ZIP, so this directory is a redistribution.

Upstream: <https://github.com/JetBrains/JetBrainsMono>

Three weights rather than the full family — xterm.js asks for regular, bold
and italic and synthesises nothing else, so the other fourteen would be dead
weight in every download.

TTF rather than WOFF2: the assets are served from disk over the
`term.local` virtual host, never over the network, so the compression WOFF2
buys is not worth adding a build-time conversion step for.
