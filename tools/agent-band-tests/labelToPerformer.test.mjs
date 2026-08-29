import { test } from 'node:test';
import assert from 'node:assert/strict';
import { labelToPerformer } from './pure.mjs';

// ── Ordering contract: variants must win over their base instrument ──
test('electric/bass variants pre-empt the acoustic base', () => {
  assert.equal(labelToPerformer('Bass guitar'), 'elec-bass');       // not 'guitar'
  assert.equal(labelToPerformer('Electric guitar'), 'elec-guitar'); // not 'guitar'
  assert.equal(labelToPerformer('Tapping (guitar technique)'), 'elec-guitar');
  assert.equal(labelToPerformer('Electric piano'), 'keytar');       // not 'piano'
  assert.equal(labelToPerformer('Hammond organ'), 'keytar');        // not 'piano'
});

test('percussion variants + genre pre-empt beat \\bdrum\\b', () => {
  assert.equal(labelToPerformer('Drum machine'), 'drum-machine'); // not 'drum'
  assert.equal(labelToPerformer('Beatbox'), 'drum-machine');
  assert.equal(labelToPerformer('Drum and bass'), 'edrum');        // not 'drum'
  assert.equal(labelToPerformer('Dubstep'), 'edrum');
});

test('the synth lookbehind excludes "Speech synthesizer" (TTS)', () => {
  assert.equal(labelToPerformer('Synthesizer'), 'synth');
  assert.equal(labelToPerformer('Speech synthesizer'), null); // no instrument match
});

// ── Base acoustic instruments still resolve ──
test('acoustic base instruments', () => {
  assert.equal(labelToPerformer('Guitar'), 'guitar');
  assert.equal(labelToPerformer('Acoustic guitar'), 'guitar');
  assert.equal(labelToPerformer('Piano'), 'piano');
  assert.equal(labelToPerformer('Organ'), 'piano');
  assert.equal(labelToPerformer('Drum'), 'drum');
  assert.equal(labelToPerformer('Cello'), 'cello');
  assert.equal(labelToPerformer('Violin, fiddle'), 'violin');
});

test('harp is guarded against harpsichord', () => {
  assert.equal(labelToPerformer('Harp'), 'harp');
  assert.equal(labelToPerformer('Harpsichord'), null);
});

// ── Vocals ──
test('male singing → a male vocal; female/neutral → null (idol-group handles those)', () => {
  assert.equal(labelToPerformer('Male singing'), 'vocal-male');
  assert.equal(labelToPerformer('Male singing', () => 'vocal-2'), 'vocal-2'); // injection honored
  assert.equal(labelToPerformer('Female singing'), null); // "female" guard blocks the male branch
  assert.equal(labelToPerformer('Choir'), null);
});

// ── Tier 2 parent-category fallbacks ──
test('parent-category fallbacks', () => {
  assert.equal(labelToPerformer('Bowed string instrument'), 'violin');
  assert.equal(labelToPerformer('Plucked string instrument'), 'guitar');
  assert.equal(labelToPerformer('Brass instrument'), 'trumpet');
  assert.equal(labelToPerformer('Percussion'), 'drum');
});

// ── Genre fallbacks (M0031) ──
test('genre fallbacks route a style to a performer', () => {
  assert.equal(labelToPerformer('House music'), 'dj-deck');
  assert.equal(labelToPerformer('Hip hop music'), 'dj-deck');
  assert.equal(labelToPerformer('Ambient music'), 'synth');
  assert.equal(labelToPerformer('Funk'), 'elec-bass');
  assert.equal(labelToPerformer('Ska'), 'trumpet');
  assert.equal(labelToPerformer('Rock music'), 'elec-guitar');
  assert.equal(labelToPerformer('Heavy metal'), 'elec-guitar');
});

test('an unmapped label → null', () => {
  assert.equal(labelToPerformer('Silence'), null);
  assert.equal(labelToPerformer('Speech'), null);
});
