const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const source = fs.readFileSync(path.resolve(__dirname,
    '../../src/SmartCar.Web/wwwroot/js/tester-note-fixes.js'), 'utf8');

test('customer signed handover remains marked signed when the file URL uses the secure document route', () => {
    const context = { document: { addEventListener() {} } };
    vm.createContext(context);
    vm.runInContext(source, context);
    const badge = { className: 'badge bg-success', textContent: 'Đã có 1 trang ký' };
    const link = { className: 'btn', textContent: 'Xem trang 1' };
    const body = {
        querySelectorAll(selector) {
            if (selector.includes('RentalSignedDocumentFiles')) return [link];
            return [];
        }
    };
    const header = { querySelector: () => badge };

    context.normalizeSignedBlock(header, body, 'Biên bản giao xe');

    assert.equal(badge.className, 'badge bg-success');
    assert.equal(badge.textContent, 'Đã ký');
    assert.equal(link.textContent, 'Trang 1');
});
