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
        Number,
        Array,
        String,
        console: { warn() {} }
    };
    context.window = context;
    vm.runInNewContext(source, context);
    return context.SmartCarDeliveryGeocoding;
}

test('search uses ArcGIS as the primary provider', async () => {
    const calls = [];
    const geocoding = client(async (url, options) => {
        calls.push({ url, options });
        return {
            ok: true,
            status: 200,
            json: async () => ({
                candidates: [{
                    address: '25 Phố Huế, Hà Nội',
                    location: { x: 105.851, y: 21.018 }
                }]
            })
        };
    });

    const results = await geocoding.search('25 Phố Huế', { lat: 21.03, lon: 105.8 });

    assert.equal(results.length, 1);
    assert.equal(results[0].display_name, '25 Phố Huế, Hà Nội');
    assert.equal(results[0].lat, '21.018');
    assert.equal(results[0].lon, '105.851');
    assert.equal(calls.length, 1);
    assert.match(calls[0].url, /^https:\/\/geocode\.arcgis\.com\/arcgis\/rest\/services\/World\/GeocodeServer\/findAddressCandidates\?/);
    assert.match(calls[0].url, /countryCode=VNM/);
    assert.match(calls[0].url, /SingleLine=25%20Ph%E1%BB%91%20Hu%E1%BA%BF/);
    assert.match(calls[0].url, /location=105\.8%2C21\.03/);
});

test('reverse uses ArcGIS as the primary provider', async () => {
    const calls = [];
    const geocoding = client(async url => {
        calls.push(url);
        return {
            ok: true,
            status: 200,
            json: async () => ({
                address: { LongLabel: '25 Phố Huế, Hà Nội, Việt Nam' },
                location: { x: 105.851, y: 21.018 }
            })
        };
    });

    const result = await geocoding.reverse(21.018, 105.851);

    assert.equal(result.display_name, '25 Phố Huế, Hà Nội, Việt Nam');
    assert.equal(calls.length, 1);
    assert.match(calls[0], /^https:\/\/geocode\.arcgis\.com\/arcgis\/rest\/services\/World\/GeocodeServer\/reverseGeocode\?/);
});

test('search falls back to Nominatim when ArcGIS is unavailable', async () => {
    const calls = [];
    const geocoding = client(async url => {
        calls.push(url);

        if (url.startsWith('https://geocode.arcgis.com/')) {
            throw new TypeError('Failed to fetch');
        }

        if (url.startsWith('https://nominatim.')) {
            return {
                ok: true,
                status: 200,
                json: async () => [
                    { lat: '21.031', lon: '105.801', display_name: 'Phố Huế, Hà Nội' }
                ]
            };
        }

        throw new Error('Unexpected server fallback');
    });

    const result = await geocoding.search('Phố Huế');

    assert.equal(result[0].display_name, 'Phố Huế, Hà Nội');
    assert.equal(calls.length, 2);
    assert.match(calls[1], /^https:\/\/nominatim\.openstreetmap\.org\/search\?/);
});

test('reverse falls back to Nominatim when ArcGIS is unavailable', async () => {
    const calls = [];
    const geocoding = client(async url => {
        calls.push(url);

        if (url.startsWith('https://geocode.arcgis.com/')) {
            return { ok: false, status: 503, json: async () => ({}) };
        }

        if (url.startsWith('https://nominatim.')) {
            return {
                ok: true,
                status: 200,
                json: async () => ({ display_name: 'Cầu Giấy, Hà Nội' })
            };
        }

        throw new Error('Unexpected server fallback');
    });

    const result = await geocoding.reverse(21.03, 105.8);

    assert.equal(result.display_name, 'Cầu Giấy, Hà Nội');
    assert.equal(calls.length, 2);
});

test('search falls back to SmartCar server when both public providers fail', async () => {
    const calls = [];
    const geocoding = client(async url => {
        calls.push(url);

        if (url.startsWith('https://')) {
            throw new TypeError('Failed to fetch');
        }

        return {
            ok: true,
            status: 200,
            json: async () => [
                { lat: '21.031', lon: '105.801', display_name: 'Hà Nội' }
            ]
        };
    });

    const result = await geocoding.search('Hà Nội', { lat: 21.03, lon: 105.8 });

    assert.equal(result[0].display_name, 'Hà Nội');
    assert.equal(calls.length, 3);
    assert.match(calls[2], /^\/api\/geocoding\/search\?/);
    assert.match(calls[2], /nearLat=21\.03/);
    assert.match(calls[2], /nearLon=105\.8/);
});

test('reverse falls back to SmartCar server when both public providers fail', async () => {
    const calls = [];
    const geocoding = client(async url => {
        calls.push(url);

        if (url.startsWith('https://')) {
            throw new TypeError('Failed to fetch');
        }

        return {
            ok: true,
            status: 200,
            json: async () => ({ display_name: 'Hà Nội' })
        };
    });

    const result = await geocoding.reverse(21.03, 105.8);

    assert.equal(result.display_name, 'Hà Nội');
    assert.equal(calls.length, 3);
    assert.match(calls[2], /^\/api\/geocoding\/reverse\?/);
});

test('server error remains visible if every provider fails', async () => {
    const geocoding = client(async url => {
        if (url.startsWith('https://')) {
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
