const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const source = fs.readFileSync(path.resolve(__dirname,
    '../../src/SmartCar.Web/wwwroot/js/delivery-geocoding.js'), 'utf8');

function client(fetchImpl) {
    const context = { fetch: fetchImpl, setTimeout, Date, Promise };
    context.window = context;
    vm.runInNewContext(source, context);
    return context.SmartCarDeliveryGeocoding;
}

test('search falls back to the browser when the server cannot reach geocoding', async () => {
    const calls = [];
    const geocoding = client(async (url, options) => {
        calls.push({ url, options });
        if (url.startsWith('/api/')) return {
            ok: false, status: 502,
            json: async () => ({ message: 'Không thể kết nối dịch vụ bản đồ.' })
        };
        return { ok: true, json: async () => [{ lat: '21.038', lon: '105.742', display_name: 'Hà Nội' }] };
    });

    const results = await geocoding.search('Hà Nội');

    assert.equal(results[0].display_name, 'Hà Nội');
    assert.equal(calls.length, 2);
    assert.match(calls[1].url, /nominatim\.openstreetmap\.org\/search\?/);
    assert.match(calls[1].url, /q=H%C3%A0%20N%E1%BB%99i/);
    assert.equal(calls[1].options.referrerPolicy, 'origin');
});

test('reverse lookup also falls back directly when the server returns 502', async () => {
    let directUrl = '';
    const geocoding = client(async url => {
        if (url.startsWith('/api/')) return { ok: false, status: 502,
            json: async () => ({ message: 'Không kết nối được.' }) };
        directUrl = url;
        return { ok: true, json: async () => ({ display_name: 'Cầu Giấy' }) };
    });

    assert.equal((await geocoding.reverse(21.038, 105.742)).display_name, 'Cầu Giấy');
    assert.match(directUrl, /\/reverse\?/);
});

test('invalid input from the server does not trigger a direct request', async () => {
    let calls = 0;
    const geocoding = client(async () => {
        calls++;
        return { ok: false, status: 400, json: async () => ({ message: 'Địa chỉ quá ngắn.' }) };
    });

    await assert.rejects(geocoding.search('a'), /Địa chỉ quá ngắn/);
    assert.equal(calls, 1);
});

test('both network paths failing reports which connections failed', async () => {
    const geocoding = client(async url => url.startsWith('/api/')
        ? { ok: false, status: 502, json: async () => ({ message: 'Máy chủ không kết nối được Nominatim.' }) }
        : { ok: false, status: 503, json: async () => ({}) });

    await assert.rejects(geocoding.search('Hà Nội'),
        /Máy chủ không kết nối được Nominatim.*Trình duyệt cũng không kết nối/);
});
