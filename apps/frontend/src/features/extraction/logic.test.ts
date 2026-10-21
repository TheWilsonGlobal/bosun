import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { ApiError } from '@/lib/api/client';
import { makeExtraction, makeField, makeRow, makeSummary } from '@/test/extractionFixtures';
import { bandOf, countBy, filterRows } from './bands';
import { buildExcludePatch, buildFieldPatch } from './edit';
import { applyPatchResult } from './merge';
import {
  describeAcceptError,
  describeExtractionFailure,
  describePatchError,
  describeUploadError,
} from './messages';
import { highlightStyle, validBbox } from './SourcePanel';
import { MAX_UPLOAD_BYTES, validateFile } from './upload';
import { pollInterval } from './useExtraction';

const api = (status: number, code: string, ext: Record<string, unknown> = {}, detail = 'd') =>
  new ApiError(status, code, detail, ext);

describe('band thresholds come from the payload', () => {
  const std = { autoAtLeast: 0.9, reviewAtLeast: 0.6 };
  it('classifies by the given thresholds, boundaries inclusive', () => {
    expect(bandOf(0.9, null, std)).toBe('auto');
    expect(bandOf(0.8999, null, std)).toBe('review');
    expect(bandOf(0.6, null, std)).toBe('review');
    expect(bandOf(0.59, null, std)).toBe('must_fix');
  });
  it('the same score lands in another band under other thresholds', () => {
    const custom = { autoAtLeast: 0.7, reviewAtLeast: 0.5 };
    expect(bandOf(0.75, 'review', custom)).toBe('auto');
    expect(bandOf(0.75, 'auto', { autoAtLeast: 0.8, reviewAtLeast: 0.4 })).toBe('review');
    expect(bandOf(0.45, 'review', { autoAtLeast: 0.8, reviewAtLeast: 0.5 })).toBe('must_fix');
  });
  it('falls back to the server band without a score, and to none for an empty cell', () => {
    expect(bandOf(null, 'review', std)).toBe('review');
    expect(bandOf(null, null, std)).toBe('none');
    expect(bandOf(undefined, 'bogus', std)).toBe('none');
  });
});

describe('row filter', () => {
  const t = { autoAtLeast: 0.9, reviewAtLeast: 0.6 };
  const rows = [
    makeRow(1, 'A', { score: 0.95, band: 'auto' }),
    makeRow(2, 'B', { score: 0.7, band: 'review' }),
    makeRow(3, 'C', { score: 0.2, band: 'must_fix' }),
    makeRow(4, 'D', { score: 0.5, band: 'must_fix', flags: ['suspected_injection'] }),
  ];
  it.each([
    ['all', [1, 2, 3, 4]],
    ['auto', [1]],
    ['review', [2]],
    ['must_fix', [3, 4]],
    ['injection', [4]],
  ] as const)('%s', (f, ids) => {
    expect(filterRows(rows, f, t).map((r) => r.id)).toEqual(ids);
  });
  it('counts per filter', () => {
    expect(countBy(rows, t)).toEqual({ all: 4, auto: 1, review: 1, must_fix: 2, injection: 1 });
  });
});

describe('edit -> PATCH body', () => {
  it('sends plain decimals in numeric columns as numbers', () => {
    expect(buildFieldPatch('alarmH', ' 95.5 ')).toEqual({ fields: { alarmH: 95.5 } });
    expect(buildFieldPatch('rangeLow', '-10')).toEqual({ fields: { rangeLow: -10 } });
  });
  it('sends ambiguous or unit-carrying numerics as typed so the server can decide', () => {
    expect(buildFieldPatch('alarmH', '1.200,5')).toEqual({ fields: { alarmH: '1.200,5' } });
    expect(buildFieldPatch('alarmH', '95 C')).toEqual({ fields: { alarmH: '95 C' } });
  });
  it('never turns text columns into numbers', () => {
    expect(buildFieldPatch('tagName', '123')).toEqual({ fields: { tagName: '123' } });
    expect(buildFieldPatch('description', ' hello ')).toEqual({ fields: { description: 'hello' } });
  });
  it('clears with null', () => {
    expect(buildFieldPatch('unit', '  ')).toEqual({ fields: { unit: null } });
  });
  it('exclude / include', () => {
    expect(buildExcludePatch(true)).toEqual({ excluded: true });
    expect(buildExcludePatch(false)).toEqual({ excluded: false });
  });
});

describe('applyPatchResult', () => {
  it('replaces the edited row AND the alsoChanged rows, and takes the server summary', () => {
    const a = makeRow(1, 'DUP', { score: 0.4, band: 'must_fix' });
    const b = makeRow(2, 'DUP', { score: 0.4, band: 'must_fix' });
    const c = makeRow(3, 'OTHER');
    const ext = makeExtraction({ rows: [a, b, c], summary: makeSummary({ rows: 3, mustFix: 2 }) });
    const a2 = {
      ...a,
      score: 0.95,
      band: 'auto',
      fields: { ...a.fields, tagName: makeField('NEW') },
    };
    const b2 = { ...b, score: 0.95, band: 'auto' };
    const next = applyPatchResult(ext, {
      row: a2,
      alsoChanged: [b2],
      summary: makeSummary({ rows: 3, auto: 3 }),
    });
    expect(next.rows.map((r) => r.band)).toEqual(['auto', 'auto', 'auto']);
    expect(next.rows[0].fields.tagName.value).toBe('NEW');
    expect(next.rows[2]).toBe(c);
    expect(next.summary.auto).toBe(3);
    expect(ext.rows[0].band).toBe('must_fix'); // input not mutated
  });
});

describe('upload validation (client side)', () => {
  it('accepts a normal PDF', () => {
    expect(validateFile({ name: 'List.PDF', size: 1000 })).toBeNull();
    expect(validateFile({ name: 'x.pdf', size: MAX_UPLOAD_BYTES })).toBeNull();
  });
  it('rejects non-PDF, empty and oversize files with specific messages', () => {
    expect(validateFile({ name: 'a.docx', size: 10 })).toMatch(/not a PDF/);
    expect(validateFile({ name: 'a.pdf.exe', size: 10 })).toMatch(/not a PDF/);
    expect(validateFile({ name: 'a.pdf', size: 0 })).toMatch(/empty/);
    expect(validateFile({ name: 'a.pdf', size: MAX_UPLOAD_BYTES + 1 })).toMatch(/limit is 20 MB/);
  });
});

describe('backend code -> message mapping', () => {
  it('upload codes each have their own text', () => {
    const msgs = [
      'no_file',
      'not_a_pdf',
      'document_too_large',
      'too_many_pages',
      'pdf_unreadable',
      'pdf_encrypted',
    ].map((c) => describeUploadError(api(422, c)).message);
    expect(new Set(msgs).size).toBe(msgs.length);
    expect(describeUploadError(api(422, 'too_many_pages', { pages: 90, max: 80 })).message).toMatch(
      /90 pages.*limit is 80/,
    );
  });

  it('accept codes: every code is distinct and specific', () => {
    const codes = [
      'unresolved_must_fix',
      'extraction_not_ready',
      'no_rows_to_accept',
      'unknown_rows',
      'rows_already_decided',
      'duplicate_tags_chosen',
    ];
    const titles = codes.map((c) => describeAcceptError(api(422, c, { rowIds: [1, 2] })).title);
    expect(new Set(titles).size).toBe(codes.length);
    const mf = describeAcceptError(api(422, 'unresolved_must_fix', { rowIds: [4, 9], count: 2 }));
    expect(mf.rowIds).toEqual([4, 9]);
    expect(mf.message).toMatch(/2 selected row/);
    const dup = describeAcceptError(api(422, 'duplicate_tags_chosen', { tags: ['A-1', 'B-2'] }));
    expect(dup.message).toContain('A-1, B-2');
    expect(
      describeAcceptError(api(409, 'extraction_not_ready', { status: 'running' })).message,
    ).toMatch(/running/);
    expect(describeAcceptError(api(409, 'rows_already_decided', { rowIds: [3] })).refetch).toBe(
      true,
    );
    expect(describeAcceptError(api(401, 'unauthorized')).title).toMatch(/sign-in/i);
  });

  it('patch codes: invalid_field carries its field; conflicts ask for a refetch', () => {
    const f = describePatchError(
      api(422, 'invalid_field', { field: 'alarmH' }, "'x' is not a number."),
    );
    expect(f).toMatchObject({ field: 'alarmH', message: "'x' is not a number.", refetch: false });
    expect(describePatchError(api(409, 'concurrent_modification')).refetch).toBe(true);
    expect(describePatchError(api(409, 'row_already_accepted')).title).toMatch(/already accepted/i);
  });

  it('extraction failure codes reuse the assistant llm messages', () => {
    expect(describeExtractionFailure('llm_rate_limited', null).title).toBe('Model is rate limited');
    expect(describeExtractionFailure('llm_auth_failed', null).title).toMatch(/API key/);
    expect(describeExtractionFailure('server_restarted', null).title).toMatch(/extraction/i);
    expect(describeExtractionFailure('weird_code', 'boom').message).toBe('boom');
  });
});

describe('polling interval', () => {
  it('polls only while queued or running', () => {
    const ms = 1500;
    expect(pollInterval(undefined, ms)).toBe(false);
    expect(pollInterval(makeExtraction({ status: 'queued' }), ms)).toBe(1500);
    expect(pollInterval(makeExtraction({ status: 'running' }), ms)).toBe(1500);
    for (const s of ['completed', 'partial', 'failed'] as const)
      expect(pollInterval(makeExtraction({ status: s }), ms)).toBe(false);
  });
});

describe('bbox highlight', () => {
  it('uses fractions with a top-left origin as percentages', () => {
    const b = validBbox([0.1, 0.2, 0.9, 0.25])!;
    const s = highlightStyle(b);
    expect(s.left).toBe('10%');
    expect(s.top).toBe('20%');
    expect(parseFloat(s.width)).toBeCloseTo(80);
    expect(parseFloat(s.height)).toBeCloseTo(5);
  });
  it('refuses unusable boxes', () => {
    expect(validBbox(null)).toBeNull();
    expect(validBbox([0.5, 0.5, 0.4, 0.6])).toBeNull();
    expect(validBbox([0, 0, 2, 1])).toBeNull();
    expect(validBbox([0, 0, NaN, 1])).toBeNull();
    expect(validBbox([0.1, 0.1, 0.2])).toBeNull();
  });
});

describe('safety: the extraction feature cannot approve or apply', () => {
  const dir = __dirname;
  const files = readdirSync(dir).filter((f) => /\.(ts|tsx)$/.test(f) && !f.includes('.test.'));

  it('has source files to check', () => {
    expect(files).toEqual(
      expect.arrayContaining([
        'api.ts',
        'DocumentsPage.tsx',
        'ExtractionPage.tsx',
        'ReviewWorkspace.tsx',
      ]),
    );
  });

  it.each(files)('%s references no approve/apply/reject endpoint or helper', (file) => {
    const src = readFileSync(join(dir, file), 'utf8');
    expect(src).not.toMatch(/approveChangeSet|applyChangeSet|rejectChangeSet|validateChangeSet/);
    expect(src).not.toMatch(/\/(approve|apply|reject|validate)\b/);
    expect(src).not.toMatch(/dangerouslySetInnerHTML|innerHTML/);
  });

  it('only api.ts talks to the network, and only to documents/extractions endpoints', () => {
    const src = readFileSync(join(dir, 'api.ts'), 'utf8');
    const urls = [...src.matchAll(/`(\/api\/[^`]*)`/g)].map((m) => m[1]);
    expect(urls.length).toBeGreaterThan(0);
    for (const u of urls)
      expect(u).toMatch(/^\/api\/(projects\/\$\{projectId\}\/documents|documents\/|extractions\/)/);
    for (const f of files.filter((x) => x !== 'api.ts'))
      expect(readFileSync(join(dir, f), 'utf8')).not.toMatch(/\bfetch\(/);
  });
});
