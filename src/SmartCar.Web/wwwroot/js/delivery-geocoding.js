(() => {
    // Giữ đúng đường chạy đã hoạt động trên master:
    // browser gọi Nominatim trực tiếp trước. Backend SmartCar chỉ là fallback
    // cho các môi trường browser không truy cập được provider.
    const directJson = async url => {
        const response = await fetch(url, {
            headers: { 'Accept': 'application/json' }
        });

        if (!response.ok) {
            throw new Error(`Nominatim trả lỗi HTTP ${response.status}.`);
        }

        return response.json();
    };

    const serverJson = async endpoint => {
        const response = await fetch(`/api/geocoding/${endpoint}`, {
            headers: { 'Accept': 'application/json' }
        });
        const result = await response.json().catch(() => null);

        if (!response.ok) {
            throw new Error(
                result?.message ||
                'Dịch vụ tìm địa chỉ tạm thời không phản hồi.');
        }

        return result;
    };

    const validFocus = focus => {
        const lat = Number(focus?.lat);
        const lon = Number(focus?.lon);

        return Number.isFinite(lat) && lat >= -90 && lat <= 90 &&
            Number.isFinite(lon) && lon >= -180 && lon <= 180
            ? { lat, lon }
            : null;
    };

    const buildViewbox = focus => {
        const point = validFocus(focus);
        if (!point) return '';

        const box = [
            point.lon - 0.55,
            point.lat - 0.45,
            point.lon + 0.55,
            point.lat + 0.45
        ].map(value => value.toFixed(5)).join(',');

        return `&viewbox=${encodeURIComponent(box)}`;
    };

    const buildServerFocus = focus => {
        const point = validFocus(focus);
        if (!point) return '';

        return `&nearLat=${encodeURIComponent(point.lat)}&nearLon=${encodeURIComponent(point.lon)}`;
    };

    const search = async (query, focus) => {
        const encoded = encodeURIComponent(query);
        const directUrl =
            'https://nominatim.openstreetmap.org/search' +
            '?format=jsonv2&limit=8&countrycodes=vn' +
            buildViewbox(focus) +
            '&q=' + encoded;

        try {
            const result = await directJson(directUrl);
            if (Array.isArray(result)) return result;
        } catch (error) {
            console.warn('Browser không tra được Nominatim search, thử qua SmartCar server:', error);
        }

        return serverJson(
            `search?q=${encoded}${buildServerFocus(focus)}`);
    };

    const reverse = async (lat, lon) => {
        const directUrl =
            'https://nominatim.openstreetmap.org/reverse' +
            '?format=jsonv2&addressdetails=1&zoom=18&accept-language=vi' +
            '&lat=' + encodeURIComponent(lat) +
            '&lon=' + encodeURIComponent(lon);

        try {
            const result = await directJson(directUrl);
            if (result?.display_name) return result;
        } catch (error) {
            console.warn('Browser không reverse-geocode được Nominatim, thử qua SmartCar server:', error);
        }

        return serverJson(
            `reverse?lat=${encodeURIComponent(lat)}&lon=${encodeURIComponent(lon)}`);
    };

    window.SmartCarDeliveryGeocoding = { search, reverse };
})();
