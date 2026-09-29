const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const { runInNewContext } = require('node:vm');
const { test } = require('node:test');

for (const page of ['Create', 'Edit']) {
    test(`${page} staff form preserves Vietnamese input while the IME composes`, () => {
        const view = readFileSync(join(__dirname, `../../src/SmartCar.Web/Views/AdminStaff/${page}.cshtml`), 'utf8');
        assert.match(view, /id="staffFullName"/, 'the employee name field is available');
        const scripts = Array.from(view.matchAll(/<script>([\s\S]*?)<\/script>/g), match => match[1]);

        const input = Object.assign(new EventTarget(), { value: '' });
        const document = {
            getElementById: id => id === 'staffFullName' ? input : null,
            querySelectorAll: () => []
        };
        scripts.forEach(script => runInNewContext(script, { document }));

        input.value = 'Nguye\u0302\u0303n Va\u0306n An';
        const event = new Event('input');
        Object.defineProperty(event, 'isComposing', { value: true });
        input.dispatchEvent(event);

        assert.equal(input.value, 'Nguye\u0302\u0303n Va\u0306n An');
    });
}
