const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const source = fs.readFileSync(path.resolve(__dirname,
    '../../src/SmartCar.Web/wwwroot/js/delivery-geocoding.js'), 'utf8');

function client(fetchImpl) {
    const context = { fetch: fetchImpl, setTimeout, clearTimeout, Promise, AbortController, Error, TypeError };
    context.window = context;
    vm.runInNewContext(source, context);
    return context.SmartCarDeliveryGeocoding;
}

test('search uses only the SmartCar server endpoint', async () => {
    const calls = [];
    const geocoding = client(async (url, options) => {
        calls.push({ url, options });
        return {
            ok: true,
            status: 200,
            json: async () => [{ lat: '21.038', lon: '105.742', display_name: 'Hà Nội' }]
        };
    });

    const results = await geocoding.search('Hà Nội', { lat: 21.03, lon: 105.8 });

    assert.equal(results[0].display_name, 'Hà Nội');
    assert.equal(calls.length, 1);
    assert.match(calls[0].url, /^\/api\/geocoding\/search\?/);
    assert.match(calls[0].url, /q=H%C3%A0%20N%E1%BB%99i/);
    assert.match(calls[0].url, /nearLat=21\.03/);
    assert.match(calls[0].url, /nearLon=105\.8/);
    assert.doesNotMatch(calls[0].url, /nominatim|photon/i);
});

test('reverse lookup uses only the SmartCar server endpoint', async () => {
    const calls = [];
    const geocoding = client(async url => {
        calls.push(url);
        return {
            ok: true,
            status: 200,
            json: async () => ({ display_name: 'Cầu Giấy, Hà Nội' })
        };
    });

    const result = await geocoding.reverse(21.038, 105.742);

    assert.equal(result.display_name, 'Cầu Giấy, Hà Nội');
    assert.equal(calls.length, 1);
    assert.match(calls[0], /^\/api\/geocoding\/reverse\?/);
});

test('server validation errors are preserved', async () => {
    const geocoding = client(async () => ({
        ok: false,
        status: 400,
        json: async () => ({ message: 'Nhập địa chỉ từ 3 đến 200 ký tự.' })
    }));

    await assert.rejects(
        geocoding.search('a'),
        /Nhập địa chỉ từ 3 đến 200 ký tự/);
});

test('server provider failure is shown without a browser Nominatim fallback', async () => {
    let calls = 0;
    const geocoding = client(async () => {
        calls++;
        return {
            ok: false,
            status: 502,
            json: async () => ({ message: 'Dịch vụ tìm địa chỉ tạm thời không phản hồi. Vui lòng thử lại sau.' })
        };
    });

    await assert.rejects(
        geocoding.search('Trâu Quỳ'),
        /Dịch vụ tìm địa chỉ tạm thời không phản hồi/);
    assert.equal(calls, 1);
});

test('browser-to-SmartCar network failure has a concise message', async () => {
    const geocoding = client(async () => {
        throw new TypeError('Failed to fetch');
    });

    await assert.rejects(
        geocoding.search('Hà Nội'),
        /Không thể kết nối máy chủ SmartCar để tìm địa chỉ/);
});

test('empty successful search remains an empty result instead of calling external providers', async () => {
    let calls = 0;
    const geocoding = client(async () => {
        calls++;
        return { ok: true, status: 200, json: async () => [] };
    });

    const results = await geocoding.search('Địa chỉ không tồn tại');

    assert.deepEqual(results, []);
    assert.equal(calls, 1);
});
