const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const { runInNewContext } = require('node:vm');
const { test } = require('node:test');

const script = readFileSync(join(__dirname, '../../src/SmartCar.Web/wwwroot/js/return-mileage-estimate.js'), 'utf8');

test('một ngày thuê, vượt 50 km hiện phụ phí 250.000 đ và cập nhật khi sửa công tơ mét', () => {
    let onInput;
    const input = {
        value: '1350',
        dataset: { handoverMileage: '1000', includedKilometers: '300', excessKmRate: '5000' },
        addEventListener(name, listener) { if (name === 'input') onInput = listener; }
    };
    const estimate = { textContent: '' };
    runInNewContext(script, {
        document: { querySelector: selector => selector === '[data-return-mileage]' ? input : estimate },
        Intl, Number, Math
    });

    assert.match(estimate.textContent, /vượt 50 km/);
    assert.match(estimate.textContent, /250\.000 đ/);

    input.value = '1250';
    onInput();
    assert.match(estimate.textContent, /vượt 0 km/);
    assert.match(estimate.textContent, /tạm tính 0 đ/);
});
