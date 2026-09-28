const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const source = fs.readFileSync(path.resolve(__dirname,
    '../../src/SmartCar.Web/wwwroot/js/delivery-geocoding.js'), 'utf8');

function client(fetchImpl) {
    const context = {
        fetch: fetchImpl,
        Promise,
        Error,
        TypeError,
        console: { warn() {} }
    };
    context.window = context;
    vm.runInNewContext(source, context);
    return context.SmartCarDeliveryGeocoding;
}

test('search restores the master behavior: browser Nominatim is primary', async () => {
    const calls = [];
    const geocoding = client(async (url, options) => {
        calls.push({ url, options });
        return {
            ok: true,
            status: 200,
            json: async () => [
                { lat: '21.038', lon: '105.742', display_name: 'Hà Nội' }
            ]
        };
    });

    const results = await geocoding.search('Hà Nội', { lat: 21.03, lon: 105.8 });

    assert.equal(results.length, 1);
    assert.equal(results[0].display_name, 'Hà Nội');
    assert.equal(calls.length, 1);
    assert.match(calls[0].url, /^https:\/\/nominatim\.openstreetmap\.org\/search\?/);
    assert.match(calls[0].url, /q=H%C3%A0%20N%E1%BB%99i/);
    assert.equal(calls[0].options.headers.Accept, 'application/json');
});

test('reverse restores the master behavior: browser Nominatim is primary', async () => {
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
    assert.match(calls[0], /^https:\/\/nominatim\.openstreetmap\.org\/reverse\?/);
});

test('search falls back to SmartCar server only when direct browser lookup fails', async () => {
    const calls = [];
    const geocoding = client(async url => {
        calls.push(url);

        if (url.startsWith('https://nominatim.')) {
            throw new TypeError('Failed to fetch');
        }

        return {
            ok: true,
            status: 200,
            json: async () => [
                { lat: '21.031', lon: '105.801', display_name: 'Phố Huế, Hà Nội' }
            ]
        };
    });

    const result = await geocoding.search('Phố Huế', { lat: 21.03, lon: 105.8 });

    assert.equal(result[0].display_name, 'Phố Huế, Hà Nội');
    assert.equal(calls.length, 2);
    assert.match(calls[1], /^\/api\/geocoding\/search\?/);
    assert.match(calls[1], /nearLat=21\.03/);
    assert.match(calls[1], /nearLon=105\.8/);
});

test('reverse falls back to SmartCar server only when direct browser lookup fails', async () => {
    const calls = [];
    const geocoding = client(async url => {
        calls.push(url);

        if (url.startsWith('https://nominatim.')) {
            return { ok: false, status: 503, json: async () => ({}) };
        }

        return {
            ok: true,
            status: 200,
            json: async () => ({ display_name: '25 Phố Huế, Hà Nội' })
        };
    });

    const result = await geocoding.reverse(21.03, 105.8);

    assert.equal(result.display_name, '25 Phố Huế, Hà Nội');
    assert.equal(calls.length, 2);
    assert.match(calls[1], /^\/api\/geocoding\/reverse\?/);
});

test('empty direct search result is accepted without making a duplicate provider request', async () => {
    let calls = 0;
    const geocoding = client(async () => {
        calls++;
        return { ok: true, status: 200, json: async () => [] };
    });

    const result = await geocoding.search('Địa chỉ không tồn tại');

    assert.equal(Array.isArray(result), true);
    assert.equal(result.length, 0);
    assert.equal(calls, 1);
});

test('if both direct and server geocoding fail, the server message is returned', async () => {
    const geocoding = client(async url => {
        if (url.startsWith('https://nominatim.')) {
            throw new TypeError('Failed to fetch');
        }

        return {
            ok: false,
            status: 502,
            json: async () => ({
                message: 'Dịch vụ tìm địa chỉ tạm thời không phản hồi. Vui lòng thử lại sau.'
            })
        };
    });

    await assert.rejects(
        geocoding.search('Trâu Quỳ'),
        /Dịch vụ tìm địa chỉ tạm thời không phản hồi/);
});
