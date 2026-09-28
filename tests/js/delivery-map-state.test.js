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
    const circles = [];
    const reverse = [], search = [], gps = [];
    const request = queue => new Promise((resolve, reject) => queue.push({ resolve, reject }));
    runInNewContext(script, {
        document: { getElementById: get, createElement: element },
        window: {
            isSecureContext: true,
            SmartCarDeliveryGeocoding: {
                reverse: () => request(reverse), search: () => request(search)
            }
        },
        navigator: {
            geolocation: { getCurrentPosition: (resolve, reject) => gps.push({ resolve, reject }) }
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
            circle: (center, options) => {
                const value = element();
                value.center = center;
                value.options = options;
                circles.push(value);
                return value;
            },
            tileLayer: element
        },
        setTimeout: fn => fn(), console: { warn() {} }, Intl, Number, Math, Error, TypeError
    });
    return { get, reverse, search, gps, markers, circles, select: (lat, lng) => map.emit('click', { latlng: { lat, lng } }) };
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

test('changing the map point replaces an old address with its GPS coordinates if lookup fails', async () => {
    const page = deliveryMap();
    const first = page.select(21.031, 105.801);
    page.reverse[0].resolve({ display_name: 'Điểm A' });
    await first;
    const second = page.select(21.032, 105.802);
    page.reverse[1].reject(new Error('Mất kết nối'));
    await second;
    assert.match(page.get('deliveryAddress').value, /21\.0320000, 105\.8020000/);
    assert.equal(page.get('delivery-location-error').classList.contains('d-none'), false);
});

test('an older address search cannot overwrite a point selected while it was loading', async () => {
    const page = deliveryMap();
    page.get('deliveryAddress').value = 'Điểm A';
    const search = page.get('find-delivery-address').emit('click');
    const selected = page.select(21.032, 105.802);
    page.reverse[0].resolve({ display_name: 'Điểm B' });
    await selected;
    page.search[0].resolve([{ lat: '21.031', lon: '105.801', display_name: 'Điểm A' }]);
    await search;
    assert.equal(page.get('deliveryAddress').value, 'Điểm B');
    assert.equal(page.get('deliveryLatitude').value, '21.0320000');
    assert.equal(page.get('find-delivery-address').disabled, false);
});

test('a successful address lookup keeps the warning for a point outside the delivery radius', async () => {
    const page = deliveryMap();
    const selected = page.select(22, 106);
    page.reverse[0].resolve({ display_name: 'Điểm ngoài vùng giao' });
    await selected;
    assert.equal(page.get('delivery-location-error').classList.contains('d-none'), false);
    assert.match(page.get('delivery-fee-amount').textContent, /Không hỗ trợ/);
});

test('a delayed GPS result cannot replace a newer point selected on the map', async () => {
    const page = deliveryMap();
    await page.get('use-current-location').emit('click');
    const selected = page.select(21.032, 105.802);
    page.reverse[0].resolve({ display_name: 'Điểm B' });
    await selected;
    const located = page.gps[0].resolve({ coords: { latitude: 21.031, longitude: 105.801, accuracy: 10 } });
    page.reverse[1]?.resolve({ display_name: 'Vị trí GPS cũ' });
    await located;
    assert.equal(page.get('deliveryLatitude').value, '21.0320000');
    assert.equal(page.get('deliveryAddress').value, 'Điểm B');
    assert.equal(page.get('use-current-location').disabled, false);
});

test('an old GPS error does not show over a valid point selected afterwards', async () => {
    const page = deliveryMap();
    await page.get('use-current-location').emit('click');
    const selected = page.select(21.032, 105.802);
    page.reverse[0].resolve({ display_name: 'Điểm B' });
    await selected;
    page.gps[0].reject({ code: 3, TIMEOUT: 3 });
    assert.equal(page.get('delivery-location-error').classList.contains('d-none'), true);
    assert.equal(page.get('use-current-location').disabled, false);
});

test('GPS remains usable when all reverse-geocoding services are unavailable', async () => {
    const page = deliveryMap();
    await page.get('use-current-location').emit('click');
    const located = page.gps[0].resolve({ coords: { latitude: 21.031, longitude: 105.801, accuracy: 10 } });
    page.reverse[0].reject(new Error('Không thể kết nối dịch vụ bản đồ. Trình duyệt cũng không kết nối được dịch vụ địa chỉ.'));
    await located;

    assert.match(page.get('deliveryAddress').value, /21\.0310000, 105\.8010000/);
    assert.equal(page.get('deliveryLatitude').value, '21.0310000');
    assert.equal(page.get('deliveryLongitude').value, '105.8010000');
    assert.doesNotMatch(page.get('delivery-location-error').textContent, /Trình duyệt cũng không kết nối/);
    assert.equal(page.get('use-current-location').disabled, false);
});

test('GPS accuracy larger than a house-level radius never labels a nearby wrong address as exact', async () => {
    const page = deliveryMap();
    await page.get('use-current-location').emit('click');
    const located = page.gps[0].resolve({ coords: { latitude: 21.031, longitude: 105.801, accuracy: 62 } });
    page.reverse[0]?.resolve({ display_name: 'Địa chỉ nhà bên cạnh' });
    await located;

    assert.equal(page.reverse.length, 0);
    assert.match(page.get('deliveryAddress').value, /21\.0310000, 105\.8010000/);
    assert.match(page.get('delivery-coordinate-text').textContent, /62.*m/);
});

test('coarse GPS keeps a draggable estimate visible but does not select or charge that point', async () => {
    const page = deliveryMap();
    await page.get('use-current-location').emit('click');
    await page.gps[0].resolve({ coords: { latitude: 21.031, longitude: 105.801, accuracy: 5000 } });

    assert.equal(page.get('deliveryLatitude').value, '');
    assert.equal(page.get('deliveryLongitude').value, '');
    assert.equal(page.get('delivery-fee-amount').textContent, 'Chọn điểm giao');
    assert.equal(page.markers.length, 2);
    assert.equal(page.markers[1].options.draggable, true);
    assert.equal(page.circles[0].options.radius, 5000);
    assert.match(page.get('delivery-location-error').textContent, /5.000 m/);

    const dragged = page.markers[1].emit('dragend', {
        target: { getLatLng: () => ({ lat: 21.032, lng: 105.802 }) }
    });
    page.reverse[0].resolve({ display_name: 'Điểm đã chỉnh trên bản đồ' });
    await dragged;

    assert.equal(page.get('deliveryLatitude').value, '21.0320000');
    assert.equal(page.get('deliveryLongitude').value, '105.8020000');
    assert.equal(page.get('deliveryAddress').value, 'Điểm đã chỉnh trên bản đồ');
});

test('address search shows multiple candidates and applies only the one the customer selects', async () => {
    const page = deliveryMap();
    const oldPoint = page.select(21.04, 105.81);
    page.reverse[0].resolve({ display_name: 'Điểm cũ' });
    await oldPoint;
    page.get('deliveryAddress').value = '25 Phố Huế';
    await page.get('deliveryAddress').emit('input');
    const searching = page.get('find-delivery-address').emit('click');
    assert.equal(page.get('deliveryLatitude').value, '', 'the old point must not be submitted with a new search');
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
    assert.equal(choices.children.length, 0);
});
