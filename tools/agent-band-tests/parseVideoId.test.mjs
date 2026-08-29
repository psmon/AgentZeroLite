import { test } from 'node:test';
import assert from 'node:assert/strict';
import { parseVideoId } from './pure.mjs';

const ID = 'dQw4w9WgXcQ'; // canonical 11-char id

test('bare 11-char id passes through', () => {
  assert.equal(parseVideoId(ID), ID);
});

test('watch URL (?v=)', () => {
  assert.equal(parseVideoId(`https://www.youtube.com/watch?v=${ID}`), ID);
});

test('watch URL with extra params (&v= form + trailing &list)', () => {
  assert.equal(parseVideoId(`https://youtube.com/watch?feature=x&v=${ID}&list=PLabc`), ID);
});

test('youtu.be short link', () => {
  assert.equal(parseVideoId(`https://youtu.be/${ID}?t=42`), ID);
});

test('/embed/ link', () => {
  assert.equal(parseVideoId(`https://www.youtube.com/embed/${ID}`), ID);
});

test('/shorts/ link', () => {
  assert.equal(parseVideoId(`https://www.youtube.com/shorts/${ID}`), ID);
});

test('/live/ link', () => {
  assert.equal(parseVideoId(`https://www.youtube.com/live/${ID}`), ID);
});

test('surrounding whitespace is trimmed', () => {
  assert.equal(parseVideoId(`   ${ID}   `), ID);
});

test('null / empty / non-string → null', () => {
  assert.equal(parseVideoId(null), null);
  assert.equal(parseVideoId(''), null);
  assert.equal(parseVideoId(undefined), null);
});

test('wrong-length bare token → null (10 and 12 chars)', () => {
  assert.equal(parseVideoId('dQw4w9WgXc'), null);   // 10
  assert.equal(parseVideoId('dQw4w9WgXcQ2'), null); // 12
});

test('a plain non-YouTube URL → null', () => {
  assert.equal(parseVideoId('https://example.com/watch?x=1'), null);
});
