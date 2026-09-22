#!/usr/bin/env node
//
// Thin launcher — forwards argv to the native agent-one binary that
// scripts/install.js placed in ../vendor/agent-one/.
//
// It stays thin on purpose: every flag, every exit code and every byte of
// output belongs to the binary, so `agent-one` behaves identically whether it
// was installed from npm, from a release ZIP, or built from source.

'use strict';

const path = require('path');
const fs = require('fs');
const { spawnSync } = require('child_process');

const PKG = '@webnori/agent-one';
const exeName = process.platform === 'win32' ? 'agent-one.exe' : 'agent-one';
const vendorBin = path.resolve(__dirname, '..', 'vendor', 'agent-one', exeName);

if (!fs.existsSync(vendorBin)) {
    console.error(`${PKG}: binary not found at ${vendorBin}`);
    console.error(`${PKG}: did the postinstall step run? Try reinstalling:`);
    console.error(`${PKG}:   npm install -g ${PKG} --force`);
    console.error(`${PKG}: or install it manually from`);
    console.error(`${PKG}:   https://github.com/psmon/AgentZeroLite/releases`);
    process.exit(1);
}

const result = spawnSync(vendorBin, process.argv.slice(2), {
    stdio: 'inherit',
    windowsHide: false
});

if (result.error) {
    console.error(`${PKG}: failed to spawn binary: ${result.error.message}`);
    process.exit(1);
}

// Preserve the child's exit code; a signal death becomes 128+n like a shell would report.
if (result.signal) process.exit(1);
process.exit(result.status ?? 0);
