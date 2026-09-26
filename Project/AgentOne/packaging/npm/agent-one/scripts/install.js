#!/usr/bin/env node
//
// postinstall: download the agent-one binary for this OS/arch from GitHub
// Releases, verify its SHA256 against checksums.txt, extract into
// ../vendor/agent-one/.
//
// User data in ~/.agent-one/ (config, sessions) is never touched — install and
// uninstall only ever manage the vendored binary.

'use strict';

const fs = require('fs');
const os = require('os');
const path = require('path');
const crypto = require('crypto');
const https = require('https');
const { execFileSync } = require('child_process');

const PKG = '@webnori/agent-one';
const REPO = process.env.AGENT_ONE_REPO || 'psmon/AgentZeroLite';
const TAG_PREFIX = 'agent-one-v';

function log(msg) { process.stdout.write(`${PKG}: ${msg}\n`); }
function warn(msg) { process.stderr.write(`${PKG}: ${msg}\n`); }
function die(msg) { warn(msg); process.exit(1); }

// 1. Opt-out, for offline CI and mirrored caches that vendor the binary themselves.
if (process.env.AGENT_ONE_SKIP_DOWNLOAD === '1') {
    log('AGENT_ONE_SKIP_DOWNLOAD=1 — skipping binary download.');
    process.exit(0);
}

// 2. platform/arch -> .NET RID
const rid = (() => {
    const arch = ({ x64: 'x64', arm64: 'arm64' })[process.arch];
    if (!arch) die(`unsupported CPU arch: ${process.arch}`);
    if (process.platform === 'linux') return `linux-${arch}`;
    if (process.platform === 'darwin') return `osx-${arch}`;
    if (process.platform === 'win32') return `win-${arch}`;
    return die(`unsupported platform: ${process.platform}`);
})();

// Intel Macs are not built (the macOS x64 runners were retired); say so rather than 404.
if (rid === 'osx-x64') {
    die('no prebuilt binary for Intel Macs (osx-x64). Build from source: ' +
        'dotnet publish Project/AgentOne/AgentOne.csproj -c Release -r osx-x64 ' +
        '(https://github.com/psmon/AgentZeroLite/tree/main/Project/AgentOne)');
}

const ext = process.platform === 'win32' ? 'zip' : 'tar.gz';
const asset = `agent-one-${rid}.${ext}`;

// 3. The npm version IS the release tag — they are published together.
const pkg = require(path.resolve(__dirname, '..', 'package.json'));
const version = process.env.AGENT_ONE_VERSION || pkg.version;
if (!version || version === '0.0.0') {
    die('package.json version is unset (0.0.0). Publish with a real version, or set AGENT_ONE_VERSION.');
}

const tag = version.startsWith(TAG_PREFIX) ? version : TAG_PREFIX + version.replace(/^v/, '');
const base = `https://github.com/${REPO}/releases/download/${tag}`;
const assetUrl = `${base}/${asset}`;
const sumsUrl = `${base}/checksums.txt`;

function get(url, redirectsLeft = 5) {
    return new Promise((resolve, reject) => {
        if (redirectsLeft < 0) return reject(new Error('too many redirects'));
        const opts = { headers: { 'User-Agent': 'agent-one-npm-installer' } };
        https.get(url, opts, (res) => {
            if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location) {
                res.resume();
                return resolve(get(res.headers.location, redirectsLeft - 1));
            }
            if (res.statusCode !== 200) {
                res.resume();
                return reject(new Error(`HTTP ${res.statusCode} for ${url}`));
            }
            resolve(res);
        }).on('error', reject);
    });
}

async function download(url, destPath) {
    const res = await get(url);
    await new Promise((resolve, reject) => {
        const out = fs.createWriteStream(destPath);
        res.pipe(out);
        out.on('finish', resolve);
        out.on('error', reject);
        res.on('error', reject);
    });
}

async function fetchText(url) {
    const res = await get(url);
    return new Promise((resolve, reject) => {
        let buf = '';
        res.on('data', (c) => { buf += c.toString('utf8'); });
        res.on('end', () => resolve(buf));
        res.on('error', reject);
    });
}

function sha256(file) {
    return crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
}

function extract(archive, destDir) {
    if (ext === 'zip') {
        try {
            execFileSync('powershell', [
                '-NoProfile', '-NonInteractive', '-Command',
                `Expand-Archive -LiteralPath '${archive}' -DestinationPath '${destDir}' -Force`
            ], { stdio: 'inherit' });
            return;
        } catch {
            execFileSync('unzip', ['-o', archive, '-d', destDir], { stdio: 'inherit' });
            return;
        }
    }
    execFileSync('tar', ['-xzf', archive, '-C', destDir], { stdio: 'inherit' });
}

async function main() {
    const root = path.resolve(__dirname, '..');
    const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'agent-one-install-'));
    const archive = path.join(tmp, asset);

    log(`release ${tag}, platform ${rid}`);
    log(`downloading ${assetUrl}`);
    try {
        await download(assetUrl, archive);
    } catch (e) {
        die(`download failed: ${e.message}\n  manual install: ${assetUrl}`);
    }

    // A checksum that cannot be fetched is a warning, but one that does not
    // match is fatal — a wrong binary is worse than no binary.
    let expected = null;
    try {
        const sums = await fetchText(sumsUrl);
        for (const line of sums.split(/\r?\n/)) {
            if (line.includes(asset)) { expected = line.trim().split(/\s+/)[0]; break; }
        }
    } catch (e) {
        warn(`could not fetch checksums.txt (${e.message}) — continuing without verification`);
    }

    if (expected) {
        const actual = sha256(archive);
        if (actual.toLowerCase() !== expected.toLowerCase()) {
            die(`checksum mismatch\n  expected: ${expected}\n  actual:   ${actual}`);
        }
        log(`sha256 verified: ${actual}`);
    }

    const vendor = path.join(root, 'vendor');
    fs.rmSync(vendor, { recursive: true, force: true });
    fs.mkdirSync(vendor, { recursive: true });

    log(`extracting into ${vendor}`);
    try {
        extract(archive, vendor);
    } catch (e) {
        die(`extraction failed: ${e.message}`);
    }

    const exeName = process.platform === 'win32' ? 'agent-one.exe' : 'agent-one';
    const finalBin = path.join(vendor, 'agent-one', exeName);
    if (!fs.existsSync(finalBin)) {
        die(`expected ${finalBin} after extraction, but it is not there`);
    }
    if (process.platform !== 'win32') fs.chmodSync(finalBin, 0o755);

    fs.rmSync(tmp, { recursive: true, force: true });
    log(`installed ${finalBin}`);
    log('try: agent-one run "hello" --provider echo');
}

main().catch((e) => die(e.stack || e.message));
