const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const { runInNewContext } = require('node:vm');
const { test } = require('node:test');

const view = readFileSync(join(__dirname, '../../src/SmartCar.Web/Views/Vehicles/DetailsV2.cshtml'), 'utf8');
const script = view.match(/<script>\s*([\s\S]*?)<\/script>/)[1];

function element() {
    const listeners = new Map();
    const classes = new Set();
    return {
        value: '', textContent: '', className: '', type: '', dataset: {}, checked: false, disabled: false, children: [],
        classList: {
            add: value => classes.add(value), remove: value => classes.delete(value),
            contains: value => classes.has(value),
            toggle: (value, enabled) => enabled ? classes.add(value) : classes.delete(value)
        },
        addEventListener(name, fn) { listeners.set(name, [...(listeners.get(name) || []), fn]); },
        on(name, fn) { this.addEventListener(name, fn); return this; },
        off(name) { listeners.delete(name); return this; },
        emit(name, event) { return Promise.all((listeners.get(name) || []).map(fn => fn(event))); },
        replaceChildren(...children) { this.children = children; },
        append(child) { this.children.push(child); },
        remove() {}, setMap() {},
        focus() {}, scrollIntoView() {}, invalidateSize() {},
        setView() { return this; }, getZoom() { return 16; },
        addTo() { return this; }, bindPopup() { return this; }, setLatLng() { return this; }
    };
}

function deliveryMap() {
    const elements = new Map();
    const get = id => {
        if (!elements.has(id)) elements.set(id, element());
        return elements.get(id);
    };
    Object.assign(get('upfront-amount').dataset, {
        baseAmount: '700000', storeLatitude: '21.03', storeLongitude: '105.8',
        includedDeliveryKm: '5', baseDeliveryFee: '100000', deliveryFeePerExtraKm: '10000',
        maxDeliveryKm: '50'
    });
    get('deliveryPickup').checked = true;

    const map = element();
    const markers = [];
    const reverse = [], search = [], gps = [];
    const timers = new Map();
    let timerId = 0;
    let watchId = 0;

    const request = queue => new Promise((resolve, reject) => queue.push({ resolve, reject }));
    const fakeSetTimeout = fn => {
        const id = ++timerId;
        timers.set(id, fn);
        return id;
    };
    const fakeClearTimeout = id => timers.delete(id);
    const runTimers = async () => {
        const pending = [...timers.entries()];
        timers.clear();
        for (const [, fn] of pending) fn();
        await Promise.resolve();
        await Promise.resolve();
    };
    const flush = async () => {
        await Promise.resolve();
        await Promise.resolve();
        await Promise.resolve();
    };

    runInNewContext(script, {
        document: { getElementById: get, createElement: element },
        window: {
            isSecureContext: true,
            SmartCarDeliveryGeocoding: {
                reverse: () => request(reverse), search: () => request(search)
            }
        },
        navigator: {
            geolocation: {
                watchPosition: (resolve, reject) => {
                    const id = ++watchId;
                    gps.push({ id, resolve, reject });
                    return id;
                },
                clearWatch() {},
                getCurrentPosition: (resolve, reject) => gps.push({ id: ++watchId, resolve, reject })
            }
        },
        L: {
            map: () => map,
            marker: (position, options) => {
                const value = element();
                value.position = position;
                value.options = options;
                markers.push(value);
                return value;
            },
            tileLayer: element
        },
        setTimeout: fakeSetTimeout,
        clearTimeout: fakeClearTimeout,
        console: { warn() {} },
        Intl, Number, Math, Error, TypeError
    });

    return {
        get, reverse, search, gps, markers, runTimers, flush,
        select: (lat, lng) => map.emit('click', { latlng: { lat, lng } })
    };
}

test('a slow reverse lookup cannot replace the address of a more recent map point', async () => {
    const page = deliveryMap();
    const first = page.select(21.031, 105.801);
    const second = page.select(21.032, 105.802);
    page.reverse[1].resolve({ display_name: 'Điểm B' });
    await second;
    page.reverse[0].resolve({ display_name: 'Điểm A' });
    await first;
    assert.equal(page.get('deliveryAddress').value, 'Điểm B');
    assert.equal(page.get('deliveryLatitude').value, '21.0320000');
});

test('a pending lookup preserves an address that the customer corrected manually', async () => {
    const page = deliveryMap();
    const selected = page.select(21.031, 105.801);
    page.get('deliveryAddress').value = 'Cổng sau, số nhà 25';
    await page.get('deliveryAddress').emit('input');
    page.reverse[0].resolve({ display_name: 'Địa chỉ gần đó' });
    await selected;
    assert.equal(page.get('deliveryAddress').value, 'Cổng sau, số nhà 25');
});

test('changing the map point keeps coordinates usable if reverse lookup fails', async () => {
    const page = deliveryMap();
    const selected = page.select(21.032, 105.802);
    page.reverse[0].reject(new Error('Mất kết nối'));
    await selected;
    assert.match(page.get('deliveryAddress').value, /21\.0320000, 105\.8020000/);
    assert.equal(page.get('deliveryLatitude').value, '21.0320000');
});

test('an older address search cannot overwrite a newer map point', async () => {
    const page = deliveryMap();
    page.get('deliveryAddress').value = 'Điểm A';
    const searching = page.get('find-delivery-address').emit('click');
    const selected = page.select(21.032, 105.802);
    page.reverse[0].resolve({ display_name: 'Điểm B' });
    await selected;
    page.search[0].resolve([{ lat: '21.031', lon: '105.801', display_name: 'Điểm A' }]);
    await searching;
    assert.equal(page.get('deliveryAddress').value, 'Điểm B');
    assert.equal(page.get('deliveryLatitude').value, '21.0320000');
});

test('GPS automatically accepts a precise fix and resolves its address', async () => {
    const page = deliveryMap();
    await page.get('use-current-location').emit('click');

    page.gps[0].resolve({ coords: { latitude: 21.031, longitude: 105.801, accuracy: 18 } });
    await page.flush();
    assert.equal(page.reverse.length, 1);

    page.reverse[0].resolve({ display_name: '25 Phố Huế, Hà Nội' });
    await page.flush();

    assert.equal(page.get('deliveryLatitude').value, '21.0310000');
    assert.equal(page.get('deliveryLongitude').value, '105.8010000');
    assert.equal(page.get('deliveryAddress').value, '25 Phố Huế, Hà Nội');
    assert.equal(page.get('use-current-location').disabled, false);
});

test('GPS waits for a better fix instead of locking the first coarse result', async () => {
    const page = deliveryMap();
    await page.get('use-current-location').emit('click');

    page.gps[0].resolve({ coords: { latitude: 21.5, longitude: 105.5, accuracy: 5000 } });
    await page.flush();
    assert.equal(page.get('deliveryLatitude').value, '');

    page.gps[0].resolve({ coords: { latitude: 21.031, longitude: 105.801, accuracy: 35 } });
    await page.flush();
    page.reverse[0].resolve({ display_name: 'Vị trí GPS tốt hơn' });
    await page.flush();

    assert.equal(page.get('deliveryLatitude').value, '21.0310000');
    assert.equal(page.get('deliveryLongitude').value, '105.8010000');
    assert.equal(page.get('deliveryAddress').value, 'Vị trí GPS tốt hơn');
});

test('coarse GPS is automatically used after the acquisition window without forcing a pin drag', async () => {
    const page = deliveryMap();
    await page.get('use-current-location').emit('click');

    page.gps[0].resolve({ coords: { latitude: 21.031, longitude: 105.801, accuracy: 5000 } });
    await page.flush();
    assert.equal(page.get('deliveryLatitude').value, '');

    await page.runTimers();
    await page.flush();
    assert.equal(page.reverse.length, 1);
    page.reverse[0].resolve({ display_name: 'Khu vực GPS nhận diện' });
    await page.flush();

    assert.equal(page.get('deliveryLatitude').value, '21.0310000');
    assert.equal(page.get('deliveryLongitude').value, '105.8010000');
    assert.equal(page.get('deliveryAddress').value, 'Khu vực GPS nhận diện');
    assert.match(page.get('delivery-location-error').textContent, /tự chọn vị trí tốt nhất/i);
    assert.doesNotMatch(page.get('delivery-location-error').textContent, /kéo ghim|5\.000 m/i);
});

test('a delayed GPS fix cannot replace a newer point selected on the map', async () => {
    const page = deliveryMap();
    await page.get('use-current-location').emit('click');

    const selected = page.select(21.032, 105.802);
    page.reverse[0].resolve({ display_name: 'Điểm B' });
    await selected;

    page.gps[0].resolve({ coords: { latitude: 21.031, longitude: 105.801, accuracy: 10 } });
    await page.flush();

    assert.equal(page.get('deliveryLatitude').value, '21.0320000');
    assert.equal(page.get('deliveryAddress').value, 'Điểm B');
    assert.equal(page.get('use-current-location').disabled, false);
});

test('GPS remains usable when reverse geocoding is unavailable', async () => {
    const page = deliveryMap();
    await page.get('use-current-location').emit('click');

    page.gps[0].resolve({ coords: { latitude: 21.031, longitude: 105.801, accuracy: 20 } });
    await page.flush();
    page.reverse[0].reject(new Error('Dịch vụ địa chỉ tạm thời không phản hồi.'));
    await page.flush();

    assert.match(page.get('deliveryAddress').value, /21\.0310000, 105\.8010000/);
    assert.equal(page.get('deliveryLatitude').value, '21.0310000');
    assert.equal(page.get('deliveryLongitude').value, '105.8010000');
    assert.doesNotMatch(page.get('delivery-location-error').textContent, /Nominatim|Photon|Failed to fetch/i);
});

test('address search shows multiple candidates and applies the one the customer selects', async () => {
    const page = deliveryMap();
    page.get('deliveryAddress').value = '25 Phố Huế';
    await page.get('deliveryAddress').emit('input');

    const searching = page.get('find-delivery-address').emit('click');
    page.search[0].resolve([
        { lat: '21.031', lon: '105.801', display_name: '25 Phố Huế, Hai Bà Trưng' },
        { lat: '21.041', lon: '105.811', display_name: '25 Phố Huế, Hoàn Kiếm' }
    ]);
    await searching;

    const choices = page.get('delivery-search-results');
    assert.equal(choices.children.length, 2);
    await choices.children[1].emit('click');
    assert.equal(page.get('deliveryAddress').value, '25 Phố Huế, Hoàn Kiếm');
    assert.equal(page.get('deliveryLatitude').value, '21.0410000');
});
