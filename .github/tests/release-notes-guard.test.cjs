// Copyright ScyllaDB, Inc. SPDX-License-Identifier: Apache-2.0
const assert = require('node:assert/strict');
const {readFileSync} = require('node:fs');
const {join} = require('node:path');
const {test} = require('node:test');
const yaml = require('js-yaml');
const guard = require('../scripts/release-notes-guard.cjs');

const repo = {owner: 'scylladb', repo: 'alternator-client-csharp'};
const label = 'omit-from-release-notes';
const file = (filename, extra = {}) => ({filename, status: 'modified', ...extra});
const workflowText = readFileSync(join(__dirname, '../workflows/label-release-notes.yml'), 'utf8');
const workflow = yaml.load(workflowText);
const steps = workflow.jobs.classify.steps;
const scripts = steps.filter(step => step.with?.script).map(step => step.with.script);
const confirmScript = steps.find(step => step.id === 'target').with.script;
const collectScript = steps.find(step => step.id === 'guard').with.script;
const finalScript = steps.at(-1).with.script;
const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;

function fixture(options = {}) {
  const files = options.files ?? [file('README.md')];
  const labels = new Set(options.labels ?? ['unrelated']);
  const calls = [];
  let gets = 0;
  let listings = 0;
  const initial = {number: 87, head: {sha: 'head'}, base: {sha: 'base'}, changed_files: files.length};
  const github = {rest: {pulls: {
    get: async params => {
      assert.deepEqual(params, {...repo, pull_number: 87});
      calls.push('get');
      gets++;
      if (options.failGetAt === gets) {
        throw Object.assign(new Error('PR lookup unavailable'), {status: options.getStatus});
      }
      return {data: {...initial, ...options.revisions?.[gets - 1]}};
    },
    listFiles: () => {}
  }, issues: {
    listLabelsOnIssue: () => {},
    removeLabel: async params => {
      assert.deepEqual(params, {...repo, issue_number: 87, name: label});
      calls.push('remove');
      if (options.cleanupFailure) throw new Error('Removal unavailable');
      const actual = [...labels].find(name => name.toLowerCase() === label);
      if (actual === undefined || !labels.delete(actual)) throw {status: 404};
    }
  }}, paginate: async (method, params) => {
    assert.equal(params.per_page, 100);
    if (method === github.rest.pulls.listFiles) {
      assert.deepEqual(params, {...repo, pull_number: 87, per_page: 100});
      calls.push('files');
      if (options.fileFailure) throw new Error('File listing unavailable');
      return options.listings?.[listings++] ?? files;
    }
    assert.equal(method, github.rest.issues.listLabelsOnIssue);
    assert.deepEqual(params, {...repo, issue_number: 87, per_page: 100});
    calls.push('labels');
    if (options.labelFailure) throw new Error('Labels unavailable');
    return options.labelMetadata ?? [...labels].map(name => ({name}));
  }};
  return {github, labels, calls};
}

async function adapter(f, hash, overrides = {}, load = () => guard) {
  const env = {PR_NUMBER: '87', CONFIRMED_PR: 'true', TARGET_OUTCOME: 'success',
    CHECKOUT_OUTCOME: 'success', INSTALL_OUTCOME: 'success',
    GUARD_OUTCOME: 'success', LABELER_OUTCOME: 'success', UNSAFE_RENAME: 'false',
    SNAPSHOT_HASH: hash, ...overrides};
  const logs = [];
  const run = new AsyncFunction('github', 'context', 'core', 'require', 'process', finalScript);
  let error;
  try {
    await run(f.github, {repo}, {info: text => logs.push(text), error: text => logs.push(text)},
      load, {env});
  } catch (caught) { error = caught; }
  return {error, logs};
}

async function confirm(f, context = {repo, eventName: 'workflow_dispatch'}, raw = '87') {
  const outputs = {};
  const run = new AsyncFunction('github', 'context', 'core', 'process', confirmScript);
  let error;
  try {
    await run(f.github, context, {setOutput: (key, value) => { outputs[key] = value; }},
      {env: {PR_NUMBER: raw}});
  } catch (caught) { error = caught; }
  return {outputs, error};
}

test('target confirmation precedes setup and persists matching manual or trusted PR identity', async () => {
  const targetIndex = steps.findIndex(step => step.id === 'target');
  assert.ok(targetIndex < steps.findIndex(step => step.id === 'checkout'));
  assert.ok(targetIndex < steps.findIndex(step => step.id === 'install'));
  const manual = fixture();
  const confirmed = await confirm(manual);
  assert.ifError(confirmed.error);
  assert.deepEqual(confirmed.outputs, {'confirmed-pr': 'true'});
  assert.deepEqual(manual.calls, ['get']);
  const event = fixture({failGetAt: 1});
  const trusted = await confirm(event, {repo, eventName: 'pull_request_target',
    payload: {pull_request: {number: 87}}});
  assert.ifError(trusted.error);
  assert.deepEqual(trusted.outputs, {'confirmed-pr': 'true'});
  assert.deepEqual(event.calls, []);
  assert.equal(steps.at(-1).env.CONFIRMED_PR, '${{ steps.target.outputs.confirmed-pr }}');
  assert.equal(steps.at(-1).env.TARGET_OUTCOME, '${{ steps.target.outcome }}');
  assert.equal(workflow.on.workflow_dispatch.inputs['pull-request'].required, true);
});

test('ordinary issues, failed lookups and mismatched identities never permit label writes', async () => {
  for (const options of [{failGetAt: 1, getStatus: 404}, {failGetAt: 1, getStatus: 403},
    {failGetAt: 1}, {revisions: [{number: 88}]}, {revisions: [{number: '87'}]},
    {revisions: [{number: undefined}]}]) {
    const f = fixture({...options, labels: [label, 'unrelated']});
    const target = await confirm(f);
    assert.ok(target.error);
    assert.deepEqual(target.outputs, {});
    const final = await adapter(f, '', {CONFIRMED_PR: target.outputs['confirmed-pr'] ?? '',
      TARGET_OUTCOME: 'failure', GUARD_OUTCOME: 'skipped', LABELER_OUTCOME: 'skipped'},
    () => {throw new Error('Unconfirmed targets must not load a helper');});
    assert.match(final.error.message, /not confirmed as a pull request/);
    assert.deepEqual(f.calls, ['get']);
    assert.deepEqual([...f.labels], [label, 'unrelated']);
  }
  for (const context of [
    {repo, eventName: 'workflow_dispatch', payload: {pull_request: {number: 87}}},
    {repo, eventName: 'pull_request_target', payload: {pull_request: {number: 88}}}
  ]) {
    const f = fixture({failGetAt: 1, getStatus: 404});
    assert.ok((await confirm(f, context)).error);
    assert.deepEqual(f.calls, ['get']);
  }
});

test('malformed manual numbers cannot establish confirmation or reach a label write', async () => {
  for (const raw of ['', '0', '01', '87\n', '9007199254740992']) {
    const f = fixture();
    const target = await confirm(f, undefined, raw);
    assert.match(target.error.message, /canonical positive safe PR number/);
    assert.deepEqual(target.outputs, {});
    const final = await adapter(f, '', {PR_NUMBER: raw, CONFIRMED_PR: ''});
    assert.ok(final.error);
    assert.deepEqual(f.calls, []);
  }
});

test('confirmed manual PRs retain helper-independent cleanup after later setup and classification failures', async () => {
  for (const key of ['CHECKOUT_OUTCOME', 'INSTALL_OUTCOME', 'GUARD_OUTCOME', 'LABELER_OUTCOME']) {
    const f = fixture({labels: [label, 'unrelated'], failGetAt: 2});
    const target = await confirm(f);
    assert.ifError(target.error);
    const final = await adapter(f, '', {CONFIRMED_PR: target.outputs['confirmed-pr'], [key]: 'failure'},
      () => {throw new Error('Later setup failed before helper availability');});
    assert.match(final.error.message, /predecessor failed/);
    assert.deepEqual(f.calls, ['get', 'remove']);
    assert.deepEqual([...f.labels], ['unrelated']);
  }
  const f = fixture({labels: [label, 'unrelated'], failGetAt: 2});
  const target = await confirm(f);
  const final = await adapter(f, '', {CONFIRMED_PR: target.outputs['confirmed-pr']},
    () => {throw new Error('Helper missing');});
  assert.match(final.error.message, /Helper missing/);
  assert.deepEqual(f.calls, ['get', 'remove']);
});

test('trusted PR confirmation survives a later API failure without another identity read', async () => {
  const f = fixture({labels: [label, 'unrelated'], failGetAt: 1});
  const target = await confirm(f, {repo, eventName: 'pull_request_target',
    payload: {pull_request: {number: 87}}});
  assert.ifError(target.error);
  const final = await adapter(f, 'snapshot', {CONFIRMED_PR: target.outputs['confirmed-pr']});
  assert.match(final.error.message, /PR lookup unavailable/);
  assert.deepEqual(f.calls, ['get', 'remove']);
  assert.deepEqual([...f.labels], ['unrelated']);
});

test('canonical policy matches mixed support files and keeps runtime or unknown paths visible', async () => {
  const files = ['UnitTests/RequestPipelineUnitTests.cs', 'IntegrationTests/IntegrationTests.cs',
    '.github/workflows/release.yml', '.github/workflows/call_jira_sync.yml', '.github/labeler.yml',
    '.github/release.yml', '.github/RELEASE_NOTES.md', 'README.md', 'scripts/release/SIGNING.md',
    'scripts/release/test_github_release.py'].map(name => file(name));
  assert.equal((await guard.snapshot(fixture({files}).github, repo, 87)).omit, true);
  for (const name of ['Helper.cs', 'ScyllaDB.Alternator.csproj', 'ScyllaDB.Alternator.sln', 'Makefile',
    'scripts/release/github_release.py', 'LICENSE', 'unknown.txt', '.github/package.json']) {
    const current = await guard.snapshot(fixture({files: [...files, file(name)]}).github, repo, 87);
    assert.equal(current.omit, false, name);
  }
});

test('renames use the actual canonical policy for both prior and current paths', async () => {
  for (const [previous, omit] of [['UnitTests/OldTests.cs', true], ['Helper.cs', false], ['unknown', false]]) {
    const current = await guard.snapshot(fixture({files: [file('UnitTests/NewTests.cs', {
      status: 'renamed', previous_filename: previous})]}).github, repo, 87);
    assert.equal(current.omit, omit);
    assert.equal(current.unsafeRename, !omit);
  }
});

test('unsupported config shapes fail instead of approximating a second policy', () => {
  for (const source of ['{}', 'omit-from-release-notes: []',
    'omit-from-release-notes: [{head-branch: [main]}]',
    'omit-from-release-notes: [{changed-files: [{any-glob-to-any-file: ["**"]}]}]',
    'omit-from-release-notes: [{changed-files: [{all-globs-to-all-files: []}]}]',
    'other-label: [{changed-files: [{all-globs-to-all-files: ["**"]}]}]']) {
    assert.throws(() => guard.policy(source), /Unsupported labeler configuration/);
  }
});

test('PR input rejects malformed, ambiguous and unsafe numbers', () => {
  assert.equal(guard.parseNumber('87'), 87);
  for (const value of ['', '0', '-1', '01', '1.0', '1e2', ' 20', '20\n',
    '20,21', '${{ arbitrary }}', '9007199254740992', undefined, 20]) {
    assert.throws(() => guard.parseNumber(value), /canonical positive safe PR number/);
  }
});

test('pagination requests full metadata and accepts a complete listing beyond one page', async () => {
  const f = fixture({files: Array.from({length: 105}, (_, i) => file(`UnitTests/Test${i}.cs`))});
  const snapshot = await guard.snapshot(f.github, repo, 87);
  assert.equal(snapshot.count, 105);
  assert.equal(snapshot.omit, true);
  assert.match(snapshot.hash, /^[0-9a-f]{64}$/);
  assert.deepEqual(f.calls, ['get', 'files', 'get']);
});

test('zero changed-file count fails before listing collection', async () => {
  const f = fixture({files: []});
  await assert.rejects(guard.snapshot(f.github, repo, 87), /Invalid PR revision or changed-file count/);
  assert.deepEqual(f.calls, ['get']);
});

test('incomplete, duplicate and malformed listings report their specific failures', async () => {
  const countError = /Changed-file listing does not match expected count/;
  const metadataError = /Incomplete or duplicate changed-file metadata/;
  const cases = [
    [{files: [], revisions: [{changed_files: 1}, {changed_files: 1}]}, countError],
    [{revisions: [{changed_files: 2}, {changed_files: 2}]}, countError],
    [{files: [file('README.md')], listings: [[]]}, countError],
    [{files: [file('README.md'), file('README.md')]}, metadataError],
    [{files: [file('README.md', {status: 'renamed'})]}, metadataError],
    [{files: [file('README.md', {status: undefined})]}, metadataError],
    [{files: [file('README.md', {previous_filename: ''})]}, metadataError],
    [{files: [{status: 'modified'}]}, metadataError],
    [{files: [null]}, metadataError],
    [{files: [file('README.md', {previous_filename: null})]}, metadataError]
  ];
  for (const [options, expected] of cases) {
    await assert.rejects(guard.snapshot(fixture(options).github, repo, 87), expected);
  }
});

test('head, base and count changes during collection are rejected', async () => {
  for (const changed of [{head: {sha: 'new'}}, {base: {sha: 'new'}}, {changed_files: 2}]) {
    const f = fixture({revisions: [{}, changed]});
    await assert.rejects(guard.snapshot(f.github, repo, 87), /revisions changed/);
  }
});

test('verification detects before/after revisions and filename, status or prior-path changes', async () => {
  const original = await guard.snapshot(fixture().github, repo, 87);
  for (const options of [
    {revisions: [{head: {sha: 'new'}}, {head: {sha: 'new'}}]},
    {revisions: [{base: {sha: 'new'}}, {base: {sha: 'new'}}]},
    {files: [file('UnitTests/NewTests.cs')]},
    {files: [file('README.md', {status: 'added'})]},
    {files: [file('README.md', {status: 'renamed', previous_filename: 'UnitTests/OldTests.cs'})]}
  ]) {
    await assert.rejects(guard.verify(fixture(options).github, repo, 87, original.hash), /snapshot changed/);
  }
  const f = fixture({labels: [label], revisions: [{}, {}, {head: {sha: 'new'}}]});
  await assert.rejects(guard.verify(f.github, repo, 87, original.hash), /revisions changed during label/);
});

test('lookup, listing and actual-label lookup failures propagate', async () => {
  await assert.rejects(guard.snapshot(fixture({failGetAt: 1}).github, repo, 87), /lookup unavailable/);
  await assert.rejects(guard.snapshot(fixture({failGetAt: 2}).github, repo, 87), /lookup unavailable/);
  await assert.rejects(guard.snapshot(fixture({fileFailure: true}).github, repo, 87), /listing unavailable/);
  const original = await guard.snapshot(fixture().github, repo, 87);
  await assert.rejects(guard.verify(fixture({labelFailure: true}).github, repo, 87, original.hash), /Labels unavailable/);
});

test('actual-label verification catches stale state after internal action skip', async () => {
  for (const [files, labels] of [[[file('README.md')], []], [[file('Helper.cs')], [label]]]) {
    const f = fixture({files, labels: [...labels, 'unrelated']});
    const original = await guard.snapshot(f.github, repo, 87);
    const {error} = await adapter(f, original.hash);
    assert.match(error.message, /Actual omission label/);
    assert.equal(f.labels.has(label), false);
    assert.equal(f.labels.has('unrelated'), true);
    assert.equal(f.calls.filter(call => call === 'remove').length, 1);
  }
});

test('failed final snapshot, lookup or label retrieval attempts cleanup', async () => {
  const original = await guard.snapshot(fixture().github, repo, 87);
  for (const options of [{files: [file('UnitTests/ChangedTests.cs')]}, {failGetAt: 1},
    {fileFailure: true}, {labelFailure: true}]) {
    const f = fixture({...options, labels: [label, 'unrelated']});
    const {error} = await adapter(f, original.hash);
    assert.ok(error);
    assert.deepEqual([...f.labels], ['unrelated']);
    assert.equal(f.calls.filter(call => call === 'remove').length, 1);
  }
});

test('successful verification checks actual labels and preserves unrelated labels', async () => {
  for (const [files, labels] of [[[file('README.md')], [label]], [[file('Helper.cs')], []]]) {
    const f = fixture({files, labels: [...labels, 'unrelated']});
    const original = await guard.snapshot(f.github, repo, 87);
    const {error, logs} = await adapter(f, original.hash);
    assert.ifError(error);
    assert.match(logs[0], /Verified head=head base=base count=1 snapshot=.*actual label checked/);
    assert.equal(f.labels.has('unrelated'), true);
    assert.ok(!f.calls.includes('remove'));
  }
});

test('actual-label identity is ASCII case-insensitive and keeps distinct names separate', async () => {
  const mixed = 'Omit-from-release-notes';
  const other = ['unrelated', `${label}-extra`, `prefix-${label}`];
  const runtime = fixture({files: [file('Helper.cs')], labels: [mixed, ...other]});
  const runtimeSnapshot = await guard.snapshot(runtime.github, repo, 87);
  const rejected = await adapter(runtime, runtimeSnapshot.hash);
  assert.match(rejected.error.message, /Actual omission label/);
  assert.deepEqual([...runtime.labels], other);
  assert.equal(runtime.calls.filter(call => call === 'remove').length, 1);

  const support = fixture({labels: [mixed, ...other]});
  const supportSnapshot = await guard.snapshot(support.github, repo, 87);
  const verified = await adapter(support, supportSnapshot.hash);
  assert.ifError(verified.error);
  assert.deepEqual([...support.labels], [mixed, ...other]);
  assert.ok(!support.calls.includes('remove'));

  const distinct = fixture({files: [file('Helper.cs')], labels: other});
  const distinctSnapshot = await guard.snapshot(distinct.github, repo, 87);
  assert.ifError((await adapter(distinct, distinctSnapshot.hash)).error);
});

test('malformed actual-label metadata cannot establish absence and attempts cleanup', async () => {
  for (const labelMetadata of [[{}], [{name: 42}], [null], {name: label}]) {
    const f = fixture({files: [file('Helper.cs')], labels: [label, 'unrelated'], labelMetadata});
    const snapshot = await guard.snapshot(f.github, repo, 87);
    const {error} = await adapter(f, snapshot.hash);
    assert.match(error.message, /Invalid actual-label metadata/);
    assert.deepEqual([...f.labels], ['unrelated']);
    assert.equal(f.calls.filter(call => call === 'remove').length, 1);
  }
});

test('intentional unsafe-rename skip removes omission label and verifies visible state', async () => {
  const f = fixture({files: [file('UnitTests/NewTests.cs', {status: 'renamed', previous_filename: 'Helper.cs'})],
    labels: [label, 'unrelated']});
  const original = await guard.snapshot(f.github, repo, 87);
  const {error, logs} = await adapter(f, original.hash, {UNSAFE_RENAME: 'true', LABELER_OUTCOME: 'skipped'});
  assert.ifError(error);
  assert.match(logs[0], /omit=false; actual label checked/);
  assert.deepEqual([...f.labels], ['unrelated']);
  assert.equal(f.calls.filter(call => call === 'remove').length, 1);
});

test('failed or skipped setup, guard and labeler clean up without requiring the helper', async () => {
  for (const key of ['CHECKOUT_OUTCOME', 'INSTALL_OUTCOME', 'GUARD_OUTCOME', 'LABELER_OUTCOME']) {
    for (const outcome of ['failure', 'skipped', 'cancelled', '']) {
      const f = fixture({labels: [label, 'unrelated']});
      const {error} = await adapter(f, '', {[key]: outcome}, () => { throw new Error('Must not require helper'); });
      assert.match(error.message, /predecessor failed or unexpectedly skipped/);
      assert.deepEqual([...f.labels], ['unrelated']);
      assert.deepEqual(f.calls, ['remove']);
    }
  }
  const f = fixture({labels: [label]});
  const {error} = await adapter(f, '', {}, () => { throw new Error('Helper missing'); });
  assert.match(error.message, /Helper missing/);
  assert.deepEqual(f.calls, ['remove']);
});

test('cleanup API failures remain visible without losing the original failure or retrying', async () => {
  const f = fixture({cleanupFailure: true});
  const result = await adapter(f, '', {INSTALL_OUTCOME: 'failure'});
  assert.match(result.error.message, /predecessor failed/);
  assert.match(result.logs[0], /cleanup failed: Removal unavailable/);
  assert.deepEqual(f.calls, ['remove']);
  const unsafe = fixture({cleanupFailure: true});
  const result2 = await adapter(unsafe, '', {UNSAFE_RENAME: 'true', LABELER_OUTCOME: 'skipped'});
  assert.match(result2.error.message, /Removal unavailable/);
  assert.deepEqual(unsafe.calls, ['remove']);
});

test('workflow adapters use environment input and emit bounded verified snapshot outputs', async () => {
  const run = new AsyncFunction('github', 'context', 'core', 'require', 'process', collectScript);
  const outputs = {};
  const f = fixture();
  await run(f.github, {repo}, {setOutput: (key, value) => { outputs[key] = value; }, info: () => {}},
    () => guard, {env: {PR_NUMBER: '87'}});
  assert.deepEqual(Object.keys(outputs), ['hash', 'unsafe-rename']);
  assert.equal(outputs['unsafe-rename'], 'false');
  const invalid = await adapter(f, '', {PR_NUMBER: '01'});
  assert.match(invalid.error.message, /canonical positive safe PR number/);
  assert.equal(steps.at(-1).if, 'always()');
  assert.equal(steps.find(step => step.id === 'checkout').with['persist-credentials'], false);
  assert.equal(steps.find(step => step.id === 'install').run, 'npm ci --ignore-scripts --prefix .github');
  const action = steps.find(step => step.id === 'labeler');
  assert.equal(action.if, "success() && steps.guard.outputs.unsafe-rename == 'false'");
  assert.equal(action.with['sync-labels'], true);
  assert.equal(action.with['pr-number'], '${{ env.PR_NUMBER }}');
  for (const script of scripts) assert.ok(!script.includes('${{'));
});

test('trusted tooling comes from the default branch for old and non-main bases and manual backfill', () => {
  const expression = steps.find(step => step.id === 'checkout').with.ref.match(/^\$\{\{\s*(.*?)\s*\}\}$/s)[1];
  const selectRef = new Function('github', `return (${expression});`);
  for (const context of [
    {sha: 'workflow-commit', event: {repository: {default_branch: 'main'},
      pull_request: {base: {sha: 'before-guard-rollout', ref: 'main'}, head: {sha: 'PR-head'}}}},
    {sha: 'maintained-branch-tip', event: {repository: {default_branch: 'main'},
      pull_request: {base: {sha: 'old-maintained-policy', ref: 'release/2.x'}, head: {sha: 'PR-head'}}}},
    {sha: 'dispatch-selected-branch', event: {repository: {default_branch: 'main'}}}
  ]) {
    assert.equal(selectRef(context), 'main');
  }
});
