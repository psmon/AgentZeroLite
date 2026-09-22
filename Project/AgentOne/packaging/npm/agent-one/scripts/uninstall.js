#!/usr/bin/env node
//
// preuninstall: remove only the vendored binary this package downloaded.
// ~/.agent-one/ (config, sessions) is the user's data and is left alone —
// reinstalling should find the same settings where they were.

'use strict';

const fs = require('fs');
const path = require('path');

const vendor = path.resolve(__dirname, '..', 'vendor');

try {
    fs.rmSync(vendor, { recursive: true, force: true });
} catch (e) {
    process.stderr.write(`@webnori/agent-one: could not remove ${vendor}: ${e.message}\n`);
}
