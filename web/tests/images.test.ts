import { test } from 'node:test';
import assert from 'node:assert/strict';
import { MAX_TOTAL_IMAGE_BYTES } from '../src/lib/images.ts';

test('defines max image bytes budget', () => {
  assert.equal(MAX_TOTAL_IMAGE_BYTES, 10 * 1024 * 1024);
});
