/*
 * Regression test for the 1.0.0.0 blank-home-screen bug.
 *
 * Custom Tabs 0.2.10.0 (TransformationPatches.HomeHtmlChunk) pastes each tab's raw ContentHtml into
 * jellyfin-web's compiled home-html chunk, inside the single-quoted JS string that holds the home
 * template, via C# Regex.Replace("$1" + html). Only the literal 'undefined' is escaped. This test ports
 * that transformation faithfully and runs it on the REAL 10.11.10 chunk (fixture), then checks that the
 * resulting JavaScript still parses and executes.
 *
 * Run: node tests/client/customtabs-chunk.js
 */
'use strict';
const fs = require('fs');
const path = require('path');
const vm = require('vm');

const chunk = fs.readFileSync(path.join(__dirname, 'fixtures/home-html.10.11.10.chunk.js'), 'utf8');
const csharp = fs.readFileSync(path.join(__dirname, '../../src/Jellyfin.Plugin.MediaVote/Core/CustomTabsInspector.cs'), 'utf8');
const snippet = JSON.parse('"' + /public const string Snippet = "((?:[^"\\]|\\.)*)";/.exec(csharp)[1] + '"');

function customTabsTransform(source, tabs) {
    const template = '<div class="tabContent pageTabContent" id="{{tab_id}}" data-index="{{tab_index}}"> {{tab_content}} </div>';
    let fin = '';
    tabs.forEach((t, i) => { fin += template.replace('{{tab_id}}', 'customTab_' + i).replace('{{tab_index}}', String(i + 2)).replace('{{tab_content}}', t); });
    fin = fin.split('\r').join(' ').split('\n').join(' ').split('  ').join(' ').split("'undefined'").join("\\'undefined\\'");
    const anchor = 'id="favoritesTab" data-index="1"> <div class="sections"></div> </div>';
    const idx = source.indexOf(anchor);
    if (idx < 0) { throw new Error('Custom Tabs anchor not found in chunk'); }
    // .NET replacement-pattern semantics for "$1{fin}": $$ -> $, $&/$n/$`/$' are substitutions.
    if (/\$(&|\d|`|')/.test(fin)) { return { error: 'tab HTML contains a .NET Regex substitution token' }; }
    return { output: source.slice(0, idx + anchor.length) + fin.replace(/\$\$/g, '$') + source.slice(idx + anchor.length) };
}

function evaluate(tabHtml) {
    const r = customTabsTransform(chunk, [tabHtml]);
    if (r.error) { return { ok: false, error: r.error }; }
    try {
        const self = { webpackChunk: [] };
        vm.runInNewContext(r.output, { self });
        const mod = self.webpackChunk[0][1];
        const exp = {};
        mod[Object.keys(mod)[0]](null, exp, { r() {} });
        return { ok: true, template: exp.default };
    } catch (e) {
        return { ok: false, error: e.name + ': ' + e.message };
    }
}

let failures = 0;
function check(cond, msg) { console.log((cond ? 'PASS ' : 'FAIL ') + msg); if (!cond) { failures++; } }

const old = evaluate(fs.readFileSync(path.join(__dirname, 'fixtures/v1.0.0.0-custom-tab.html'), 'utf8'));
check(!old.ok, 'reproduced: the 1.0.0.0 tab breaks the 10.11.10 home page chunk (' + old.error + ')');

const ok = evaluate(snippet);
check(ok.ok, '1.0.1.0 snippet keeps the home page chunk valid JavaScript');
check(ok.ok && ok.template.includes('data-mediavote-mount="tab"') && ok.template.includes('Media Vote is loading'), 'snippet ends up in the home template with its mount element and plain fallback text');
check(ok.ok && ok.template.includes('id="customTab_0"'), 'tab container rendered by Custom Tabs template');
check(!/[\'\\$<]script/i.test(snippet) && !/[\'\\$]/.test(snippet), 'snippet has no script, single quote, backslash or dollar sign');
check(!evaluate("<p>It doesn't work</p>").ok, 'documented hazard: any apostrophe in any Custom Tabs tab breaks the home page on 0.2.x');
check(evaluate('').ok, 'empty tab content keeps the page valid');

console.log(failures ? failures + ' check(s) failed' : 'All Custom Tabs chunk checks passed');
process.exit(failures ? 1 : 0);
