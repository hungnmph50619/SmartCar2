const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const source = fs.readFileSync(path.resolve(__dirname,
    '../../src/SmartCar.Web/wwwroot/js/delivery-geocoding.js'), 'utf8');

function client(fetchImpl) {
    const context = { fetch: fetchImpl, setTimeout, clearTimeout, Date, Promise, AbortController };
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
        /Máy chủ không kết nối được Nominatim.*Dịch vụ địa chỉ dự phòng không phản hồi/);
});

test('network failures show a useful browser message instead of the raw fetch exception', async () => {
    const geocoding = client(async url => {
        if (url.startsWith('/api/')) return {
            ok: false, status: 502,
            json: async () => ({ message: 'Máy chủ không kết nối được dịch vụ tìm địa chỉ.' })
        };
        throw new TypeError('Failed to fetch');
    });

    await assert.rejects(geocoding.search('Trâu Quỳ'), error => {
        assert.match(error.message, /Máy chủ không kết nối được dịch vụ tìm địa chỉ/);
        assert.match(error.message, /Trình duyệt không kết nối được Nominatim/);
        assert.doesNotMatch(error.message, /Failed to fetch/);
        return true;
    });
});

test('search uses an independent geocoder if Nominatim is unreachable from server and browser', async () => {
    const geocoding = client(async url => {
        if (url.startsWith('/api/')) return {
            ok: false, status: 502,
            json: async () => ({ message: 'Không thể kết nối dịch vụ bản đồ.' })
        };
        if (url.startsWith('https://nominatim.')) throw new TypeError('Network error');
        if (url.startsWith('https://photon.komoot.io/api')) return {
            ok: true,
            json: async () => ({ features: [{ geometry: { coordinates: [105.8, 21.03] }, properties: { name: 'Hà Nội', countrycode: 'VN' } }] })
        };
        throw new Error(`Unexpected request: ${url}`);
    });

    const results = await geocoding.search('Hà Nội');
    assert.equal(results[0].lat, '21.03');
    assert.equal(results[0].lon, '105.8');
    assert.match(results[0].display_name, /Hà Nội/);
});

test('search falls back to Photon when Nominatim responds successfully with no matches', async () => {
    const requests = [];
    const geocoding = client(async url => {
        requests.push(url);
        if (url.startsWith('/api/')) return { ok: true, json: async () => [] };
        if (url.startsWith('https://photon.komoot.io/api')) return {
            ok: true,
            json: async () => ({ features: [
                { geometry: { coordinates: [105.81, 21.04] }, properties: {
                    countrycode: 'VN', housenumber: '25', street: 'Phố Huế', district: 'Hai Bà Trưng'
                } },
                { geometry: { coordinates: [105.82, 21.05] }, properties: {
                    countrycode: 'VN', housenumber: '25', street: 'Phố Huế', district: 'Hoàn Kiếm'
                } }
            ] })
        };
        throw new Error(`Unexpected request: ${url}`);
    });

    const results = await geocoding.search('25 Phố Huế', { lat: 21.03, lon: 105.8 });

    assert.equal(results.length, 2);
    assert.match(results[0].display_name, /25 Phố Huế/);
    assert.match(requests[0], /nearLat=21\.03.*nearLon=105\.8/);
    assert.match(requests[1], /countrycode=VN/);
    assert.match(requests[1], /limit=5/);
    assert.match(requests[1], /lat=21\.03.*lon=105\.8/);
});

test('reverse Photon fallback searches within 100 metres of the selected GPS point', async () => {
    let photonUrl = '';
    const geocoding = client(async url => {
        if (url.startsWith('/api/')) return { ok: false, status: 404, json: async () => ({ message: 'Không có địa chỉ.' }) };
        if (url.startsWith('https://nominatim.')) return { ok: false, status: 404, json: async () => ({}) };
        photonUrl = url;
        return { ok: true, json: async () => ({ features: [{
            geometry: { coordinates: [105.8, 21.03] },
            properties: { countrycode: 'VN', housenumber: '25', street: 'Phố Huế' }
        }] }) };
    });

    const result = await geocoding.reverse(21.03, 105.8);

    assert.equal(result.display_name, '25 Phố Huế');
    assert.match(photonUrl, /radius=0\.1/);
});
